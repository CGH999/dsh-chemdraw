import { execFile } from 'node:child_process'
import { existsSync, mkdirSync, unlinkSync, writeFileSync } from 'node:fs'
import { readFile, rm, writeFile } from 'node:fs/promises'
import { randomUUID } from 'node:crypto'
import { fileURLToPath } from 'node:url'
import os from 'node:os'
import path from 'node:path'
import { promisify } from 'node:util'

const execFileAsync = promisify(execFile)

const BRIDGE = fileURLToPath(new URL('./bin/ChemBridge.exe', import.meta.url))
const BRIDGE_DLL = fileURLToPath(new URL('./bin/CambridgeSoft.ChemScript.dll', import.meta.url))

/**
 * Scratch directory for generated CDXML/PNG caches. The package may live outside the session
 * workspace, and a sandboxed child process can only write inside that workspace, so probe a
 * short list of candidates instead of assuming the package folder is writable.
 */
let scratchDir = null
function scratch() {
  if (scratchDir) return scratchDir
  const candidates = []
  if (process.env.DSH_CHEMDRAW_VAR) candidates.push(process.env.DSH_CHEMDRAW_VAR)
  candidates.push(fileURLToPath(new URL('./var/', import.meta.url)))
  candidates.push(path.join(os.tmpdir(), 'dsh-chemdraw-var'))
  for (const dir of candidates) {
    try {
      mkdirSync(dir, { recursive: true })
      const probe = path.join(dir, `.probe-${process.pid}`)
      writeFileSync(probe, 'ok')
      unlinkSync(probe)
      scratchDir = dir
      return dir
    } catch {
      // try the next candidate
    }
  }
  throw new Error('chemdraw: no writable scratch directory (set DSH_CHEMDRAW_VAR)')
}

export const inject = ['tools']

// ---------------------------------------------------------------- bridge I/O

async function bridgeOnce(argv, { timeoutMs = 180000, signal } = {}) {
  if (!existsSync(BRIDGE)) throw new Error(`ChemBridge.exe is missing at ${BRIDGE}; build it with bridge/build.ps1`)
  if (!existsSync(BRIDGE_DLL)) throw new Error('CambridgeSoft.ChemScript.dll is missing next to the bridge; rebuild with bridge/build.ps1')

  const workspace = scratch()
  const outFile = path.join(workspace, `.bridge-${randomUUID()}.json`)
  const env = { ...process.env, CHEMDRAW_WORK_ROOT: workspace }
  // ChemDraw 的安装位置由 CHEMDRAW_HOME 指定；未设置时桥接会给出明确报错

  let failure = null
  try {
    // stdio must not be piped here: the harness forbids pipe handles for child processes,
    // so the bridge writes its JSON to a file instead of stdout.
    await execFileAsync(BRIDGE, [...argv, '--json-out', outFile], {
      timeout: timeoutMs,
      signal,
      env,
      cwd: path.dirname(BRIDGE),
      windowsHide: true,
      stdio: 'ignore',
    })
  } catch (error) {
    failure = error
  }

  if (existsSync(outFile)) {
    try {
      return JSON.parse(await readFile(outFile, 'utf8'))
    } finally {
      await rm(outFile, { force: true })
    }
  }

  const error = new Error(
    `ChemDraw bridge produced no result${failure ? ` (${failure.code ?? 'spawn failed'}${failure.status !== undefined ? ` / exit ${failure.status}` : ''})` : ''}. Run chemdraw_doctor to check the installation.`,
  )
  // Reads are safe to retry: a cold ChemDraw start can fail the first call. Writes never are.
  const readOnly = ['inspect', 'render', 'threed', 'doctor'].includes(argv[0])
  error.retryable = readOnly && Boolean(
    failure && (failure.killed || failure.code === 'ETIMEDOUT' || failure.code === 'ABORT_ERR' || failure.signal === 'SIGTERM'),
  )
  throw error
}

/** Run the bridge, retrying once when the first hidden ChemDraw start is aborted. */
async function runBridge(argv, options = {}) {
  try {
    return await bridgeOnce(argv, options)
  } catch (error) {
    if (!error.retryable) throw error
    return bridgeOnce(argv, { ...options, timeoutMs: Math.round((options.timeoutMs ?? 180000) * 1.5) })
  }
}

/** Bridge failures must reach the user as their real message, not as a serialisation error. */
async function callBridge(argv, options = {}) {
  const data = await runBridge(argv, options)
  if (data && data.ok === false && !data.verification) {
    throw new Error(data.error || `ChemDraw bridge rejected the request (${data.command ?? 'unknown'})`)
  }
  return data
}

function compact(object) {
  const result = {}
  for (const [key, value] of Object.entries(object)) {
    if (value !== undefined) result[key] = value
  }
  return result
}

// ---------------------------------------------------------------- rendering

function clip(value, max) {
  const text = String(value ?? '')
  return text.length > max ? `${text.slice(0, max - 1)}…` : text
}

function cell(value) {
  return String(value ?? '').replace(/\|/g, '\\|')
}

function renderInspect(data, detail = 'full') {
  const v = data.validation ?? {}
  const structures = data.structures ?? []
  const lines = []
  lines.push(`# ${data.file}`)
  lines.push(
    `${structures.length} structures · ${data.document?.captions ?? 0} captions · ${data.document?.arrows ?? 0} arrows · parsed ${v.parsed ?? 0} · warnings ${(v.warnings ?? []).length}`,
  )
  if (data.artifacts?.png) lines.push(`page image: ${data.artifacts.png}`)
  lines.push('')
  lines.push('| # | label | formula | MW | name |')
  lines.push('|---|---|---|---|---|')
  for (const s of structures) {
    const label = s.label ? `**${cell(s.label)}**` : ''
    const formula = s.formula ?? '?'
    const mw = s.mw ?? '?'
    lines.push(`| ${s.id} | ${label} | ${formula} | ${mw} | ${cell(clip(s.name || s.smiles, 48))} |`)
  }
  lines.push('')
  if (detail === 'table') {
    lines.push('_Names are truncated in table mode; pass detail:"full" for SMILES / InChI / full IUPAC name per structure._')
    lines.push('')
  }

  if (detail !== 'table') {
    for (const s of structures) {
      const head = `#${s.id}${s.label ? ` ${s.label}` : ''} · ${s.formula ?? '?'} · ${s.mw ?? '?'}`
      lines.push(head)
      if (s.smiles) lines.push(`  SMILES ${s.smiles}`)
      if (s.inchi) lines.push(`  InChI  ${s.inchi}`)
      if (s.name) lines.push(`  name   ${s.name}`)
      const ez = (s.depiction?.stereo_double_bonds ?? []).map((e) => e.geometry_drawn).join(',')
      const stereo = []
      if (ez) stereo.push(`double bonds as drawn: ${ez}`)
      if (s.depiction?.wedge_bonds) stereo.push(`${s.depiction.wedge_bonds} wedge bonds`)
      if (stereo.length) lines.push(`  geometry ${stereo.join(' · ')}`)
      if (s.annotation?.text) lines.push(`  drawing says "${s.annotation.text.replace(/\n/g, ' / ')}"${s.annotation.mw_matches === false ? ' — MW MISMATCH' : ''}`)
      if (s.interpretable === false) lines.push('  ! unrecognised abbreviation group: formula suppressed')
      lines.push('')
    }
  }

  const captions = data.captions ?? []
  if (captions.length) {
    lines.push(`Captions (${captions.length}): ${captions.map((c) => c.text.replace(/\n/g, ' / ')).join(' · ')}`)
    lines.push('')
  }
  const reactions = data.reactions ?? []
  if (reactions.length) {
    lines.push('Reactions:')
    for (const r of reactions) {
      const left = (r.reactants ?? []).join(',') || '?'
      const right = (r.products ?? []).join(',') || '?'
      const conditions = (r.conditions ?? []).join('; ')
      lines.push(`- arrow ${r.arrow}: ${left} → ${right}${conditions ? `  [${conditions}]` : ''}`)
    }
    lines.push('')
  }
  const warnings = v.warnings ?? []
  if (warnings.length) {
    lines.push('Warnings:')
    for (const w of warnings) lines.push(`- [${w.code}]${w.structure ? ` #${w.structure}` : ''} ${w.message}`)
    lines.push('')
  }
  lines.push(`timing: convert ${data.timing_ms?.convert ?? 0} ms, analyse ${data.timing_ms?.analyze ?? 0} ms`)
  return lines.join('\n')
}

function renderWrite(data, dryRun) {
  const lines = []
  const state = data.applied ? 'applied' : dryRun || data.dry_run ? 'dry run (file untouched)' : 'not applied'
  lines.push(`${data.ok ? 'OK' : 'REJECTED'} · ${state}`)
  if (data.file) lines.push(`file: ${data.file}`)
  for (const c of data.changes ?? []) {
    // The saved file's ids are what the next call must use, so lead with them.
    const savedId = c.id_after_save
    const idText = savedId !== undefined ? `#${savedId}` : `#${c.id}`
    const memory = savedId !== undefined && savedId !== c.id ? ` (in-memory #${c.id})` : ''
    if (c.op === 'add') lines.push(`- added ${idText}${memory} ${c.formula ?? '?'} (MW ${c.mw ?? '?'})${c.label ? ` labelled ${c.label}` : ''}`)
    else if (c.op === 'delete') {
      const shared = c.label_match_count ? `; label matched ${c.label_match_count} structures` : ''
      lines.push(`- deleted #${c.id} ${c.formula ?? '?'} (captions removed: ${c.captions_removed ?? 0}${shared})`)
    }
    else if (c.op === 'replace') lines.push(`- replaced ${idText}${memory} ${c.old_formula ?? '?'} → ${c.formula ?? '?'} (MW ${c.mw ?? '?'}, captions refreshed: ${c.captions_refreshed ?? 0})`)
    else if (c.op === 'annotate') lines.push(`- annotation "${c.text}" at ${(c.at ?? []).join(',')}`)
    else if (c.op === 'move') lines.push(`- moved #${c.id} by (${c.dx}, ${c.dy})`)
    else if (c.op === 'arrow') lines.push(`- arrow ${(c.from ?? []).join(',')} → ${(c.to ?? []).join(',')}`)
    if (c.id_match === 'inchi_ambiguous' && c.op !== 'delete') {
      lines.push(`  note: several structures share this identity (ids ${(c.same_inchi_ids ?? []).join(', ')}); delete by label to be unambiguous`)
    }
  }
  const v = data.verification ?? {}
  if (v.expected !== undefined) {
    lines.push(`verification: expected ${v.expected} / actual ${v.actual} structures, ${v.verified ? 'matched' : 'MISMATCH'} (${v.compared_by})`)
  }
  for (const m of v.missing ?? []) lines.push(`  missing: ${m}`)
  for (const u of v.unexpected ?? []) lines.push(`  unexpected: ${u}`)
  if (data.backup) lines.push(`backup: ${data.backup}`)
  if (data.artifacts?.preview_png) lines.push(`preview image: ${data.artifacts.preview_png}`)
  if (data.error) lines.push(`error: ${data.error}`)
  if (data.applied) lines.push('ids in the saved file are renumbered by ChemDraw; re-run chemdraw_inspect before any later edit.')
  return lines.join('\n')
}

function renderThreeD(data) {
  if (!data.ok) return `3D generation failed: ${data.error ?? 'unknown error'}`
  const lines = [`# 3D conformers (MM2) · ${data.file}`]
  for (const s of data.structures ?? []) {
    if (!s.ok) {
      lines.push(`- #${s.id} failed: ${s.error}`)
      continue
    }
    lines.push(
      `- #${s.id} ${s.formula ?? '?'}: energy ${s.energy_kcal} kcal/mol · planarity RMSD ${s.planarity_rmsd} Å · max torsion ${s.max_torsion_deg}° · span ${s.max_atom_distance} Å (${s.ms} ms)`,
    )
  }
  return lines.join('\n')
}

// ---------------------------------------------------------------- tools

const FILE = { type: 'string', description: 'Absolute path to a .cdx (or .cdxml) file.' }
const DRY = { type: 'boolean', description: 'Preview the change without touching the file (default true).' }
const POINT = { type: 'string', description: 'A point in document points, formatted "x,y".' }

function tool(definition) {
  return {
    name: definition.name,
    description: definition.description,
    parameters: definition.parameters,
    timeoutMs: definition.timeoutMs ?? 240000,
    async execute(args, exec) {
      return definition.run(args, exec)
    },
    output: {
      schema: { type: 'object', additionalProperties: true },
      render(_args, value) {
        const summary = typeof value?.summary === 'string' ? value.summary : JSON.stringify(value)
        return [{ type: 'text', text: summary }]
      },
    },
  }
}

function writeArgs(argv, { dry_run = true }) {
  if (dry_run !== false) argv.push('--dry-run')
  return argv
}

export function apply(ctx) {
  const definitions = [
    tool({
      name: 'chemdraw_inspect',
      description:
        'Read a ChemDraw .cdx/.cdxml file and return its chemistry: every structure with SMILES, InChI, molecular formula, exact mass and IUPAC name, plus the 2D depiction geometry (stereogenic double bonds as drawn, wedge bonds, bond-length outliers), the text captions, the reaction arrows and a per-structure thumbnail path. Only captions that contain a molecular weight are associated with a structure; a lone "Chemical Formula: …" stays unassociated. Duplicate labels are reported as a label_duplicate warning. Reads only; never modifies the file.',
      parameters: {
        type: 'object',
        properties: {
          file: FILE,
          include_thumbnails: { type: 'boolean', description: 'Crop one PNG per structure (default true).' },
          detail: {
            type: 'string',
            enum: ['full', 'table'],
            description: 'full: per-structure SMILES/InChI block. table: compact table only, for very large files.',
          },
        },
        required: ['file'],
      },
      async run({ file, include_thumbnails = true, detail = 'full' }, exec) {
        const argv = ['inspect', file]
        if (include_thumbnails === false) argv.push('--no-thumbs')
        const data = await callBridge(argv, { signal: exec?.signal, timeoutMs: 180000 })
        return compact({
          summary: renderInspect(data, detail),
          ok: data.ok,
          structures: data.structures,
          captions: data.captions,
          reactions: data.reactions,
          validation: data.validation,
          artifacts: data.artifacts,
        })
      },
    }),
    tool({
      name: 'chemdraw_render',
      description:
        'Render a ChemDraw file to images: the whole page plus a cropped PNG per structure. Returns image paths that can be shown inline. An id that does not exist is reported as an error rather than an empty result.',
      parameters: {
        type: 'object',
        properties: {
          file: FILE,
          ids: { type: 'string', description: 'Comma separated structure ids to keep (default: all).' },
        },
        required: ['file'],
      },
      async run({ file, ids }, exec) {
        const argv = ['render', file]
        if (ids) argv.push('--ids', ids)
        const data = await callBridge(argv, { signal: exec?.signal, timeoutMs: 180000 })
        const lines = [`# ${file}`, `page image: ${data.artifacts?.png ?? '(none)'}`]
        for (const s of data.structures ?? []) lines.push(`- #${s.id}${s.label ? ` ${s.label}` : ''}: ${s.thumbnail ?? '(no thumbnail)'}`)
        return compact({ summary: lines.join('\n'), ok: true, artifacts: data.artifacts, structures: data.structures })
      },
    }),
    tool({
      name: 'chemdraw_3d',
      description:
        'Generate MM2 3D conformers for structures in a ChemDraw file and report spatial descriptors: conformer energy, planarity RMSD, largest torsional angle and molecular span. Use when the 2D drawing alone is not enough (conjugation, planarity, donor–acceptor geometry). An id that does not exist is reported as an error.',
      parameters: {
        type: 'object',
        properties: {
          file: FILE,
          ids: { type: 'string', description: 'Comma separated structure ids (default: all).' },
        },
        required: ['file'],
      },
      async run({ file, ids }, exec) {
        const argv = ['threed', file]
        if (ids) argv.push('--ids', ids)
        const data = await callBridge(argv, { signal: exec?.signal, timeoutMs: 300000 })
        return compact({ summary: renderThreeD(data), ok: data.ok, structures: data.structures })
      },
    }),
    tool({
      name: 'chemdraw_add',
      description:
        'Add a new structure to a ChemDraw file from SMILES. The structure is laid out, placed in free space (or at an explicit position), and optionally labelled. Labels must be 1-12 characters, start with a letter or digit, and may otherwise contain letters, digits, "-", "_" or "."; anything else is rejected because the reader could not pair it back. Writes are verified by re-reading the file, a .bak copy is kept, and nothing is written unless dry_run is false.',
      parameters: {
        type: 'object',
        properties: {
          file: FILE,
          smiles: { type: 'string', description: 'SMILES of the structure to add.' },
          label: { type: 'string', description: 'Caption placed next to the new structure, e.g. "5" or "BAX-2".' },
          at: POINT,
          props: { type: 'boolean', description: 'Also add Chemical Formula / Molecular Weight captions.' },
          dry_run: DRY,
        },
        required: ['file', 'smiles'],
      },
      async run({ file, smiles, label, at, props, dry_run = true }, exec) {
        const argv = ['add', file, '--smiles', smiles]
        if (label) argv.push('--label', label)
        if (at) argv.push('--at', at)
        if (props) argv.push('--props')
        const data = await callBridge(writeArgs(argv, { dry_run }), { signal: exec?.signal, timeoutMs: 240000 })
        return compact({ summary: renderWrite(data, dry_run !== false), ok: data.ok, ...data })
      },
    }),
    tool({
      name: 'chemdraw_find',
      description:
        'List the structures of a ChemDraw file that match a label, an id, a formula or a SMILES fragment, with their ids and thumbnail paths. Use it to resolve an ambiguous label (several structures can share one) before deleting or replacing, and to discover the id to edit.',
      parameters: {
        type: 'object',
        properties: {
          file: FILE,
          label: { type: 'string', description: 'Exact label, case-insensitive.' },
          index: { type: 'integer', description: 'Exact structure id.' },
          formula: { type: 'string', description: 'Substring of the molecular formula, e.g. "C2H6O".' },
          smiles: { type: 'string', description: 'Substring of the SMILES, e.g. "c1ccccc1".' },
        },
        required: ['file'],
      },
      async run({ file, label, index, formula, smiles }, exec) {
        const argv = ['find', file]
        if (label) argv.push('--label', label)
        if (typeof index === 'number') argv.push('--index', String(index))
        if (formula) argv.push('--formula', formula)
        if (smiles) argv.push('--smiles', smiles)
        const data = await callBridge(argv, { signal: exec?.signal, timeoutMs: 240000 })
        const lines = [`# ${file}`, `${data.match_count ?? 0} match(es)`]
        if (data.filter && Object.values(data.filter).some((v) => v !== null && v !== undefined)) {
          lines.push(`filter: ${JSON.stringify(data.filter)}`)
        }
        lines.push('')
        lines.push('| # | label | formula | MW | thumbnail |')
        lines.push('|---|---|---|---|---|')
        for (const m of data.matches ?? []) {
          lines.push(`| ${m.id} | ${m.label ? `**${cell(m.label)}**` : ''} | ${m.formula ?? '?'} | ${m.mw ?? '?'} | ${m.thumbnail ? 'yes' : '-'} |`)
        }
        if ((data.matches ?? []).length === 0) lines.push('_nothing matched; widen the filter or inspect the file first_')
        return compact({ summary: lines.join('\n'), ok: true, match_count: data.match_count, matches: data.matches, artifacts: data.artifacts })
      },
    }),
    tool({
      name: 'chemdraw_delete',
      description:
        'Delete a structure (and the captions it owns) from a ChemDraw file, selected by its id or by its label. Several structures may share a label: pass nth (1-based, default 1) to choose, and use chemdraw_find to see which one is which. Verified by re-reading the file; a .bak copy is kept; dry_run is the default.',
      parameters: {
        type: 'object',
        properties: {
          file: FILE,
          index: { type: 'integer', description: 'Structure id from chemdraw_inspect.' },
          label: { type: 'string', description: 'Structure label, used when index is omitted.' },
          nth: { type: 'integer', description: 'Which match to delete when several structures share the label (1-based, default 1).' },
          dry_run: DRY,
        },
        required: ['file'],
      },
      async run({ file, index, label, nth, dry_run = true }, exec) {
        const argv = ['delete', file]
        if (typeof index === 'number') argv.push('--index', String(index))
        else if (label) {
          argv.push('--label', label)
          if (typeof nth === 'number' && nth > 1) argv.push('--nth', String(nth))
        } else throw new Error('chemdraw_delete needs either index or label')
        const data = await callBridge(writeArgs(argv, { dry_run }), { signal: exec?.signal, timeoutMs: 240000 })
        return compact({ summary: renderWrite(data, dry_run !== false), ok: data.ok, ...data })
      },
    }),
    tool({
      name: 'chemdraw_replace',
      description:
        'Replace one structure in a ChemDraw file with a new structure built from SMILES, keeping its position and refreshing the "Molecular Weight" text of the captions that structure owns. Note: a caption containing only "Chemical Formula: …" (no molecular weight) is not associated with any structure and will not be rewritten. Labels follow the same rule as chemdraw_add. Verified by re-reading the file; dry_run is the default.',
      parameters: {
        type: 'object',
        properties: {
          file: FILE,
          index: { type: 'integer', description: 'Structure id to replace.' },
          smiles: { type: 'string', description: 'SMILES of the replacement structure.' },
          label: { type: 'string', description: 'Optional new label.' },
          props: { type: 'boolean', description: 'Add Chemical Formula / Molecular Weight captions if none exist.' },
          dry_run: DRY,
        },
        required: ['file', 'index', 'smiles'],
      },
      async run({ file, index, smiles, label, props, dry_run = true }, exec) {
        const argv = ['replace', file, '--index', String(index), '--smiles', smiles]
        if (label) argv.push('--label', label)
        if (props) argv.push('--props')
        const data = await callBridge(writeArgs(argv, { dry_run }), { signal: exec?.signal, timeoutMs: 240000 })
        return compact({ summary: renderWrite(data, dry_run !== false), ok: data.ok, ...data })
      },
    }),
    tool({
      name: 'chemdraw_annotate',
      description:
        'Add a text caption to a ChemDraw file, either attached to a structure (placed under it) or at an explicit position. Captions are written as plain text, not as chemical formula.',
      parameters: {
        type: 'object',
        properties: {
          file: FILE,
          text: { type: 'string', description: 'Caption text.' },
          index: { type: 'integer', description: 'Structure id to attach the caption to.' },
          at: POINT,
          dry_run: DRY,
        },
        required: ['file', 'text'],
      },
      async run({ file, text: caption, index, at, dry_run = true }, exec) {
        const argv = ['annotate', file, '--text', caption]
        if (typeof index === 'number') argv.push('--index', String(index))
        if (at) argv.push('--at', at)
        const data = await callBridge(writeArgs(argv, { dry_run }), { signal: exec?.signal, timeoutMs: 240000 })
        return compact({ summary: renderWrite(data, dry_run !== false), ok: data.ok, ...data })
      },
    }),
    tool({
      name: 'chemdraw_move',
      description: 'Move one structure (together with the captions it owns) by a delta or to an absolute position. dry_run is the default.',
      parameters: {
        type: 'object',
        properties: {
          file: FILE,
          index: { type: 'integer', description: 'Structure id to move.' },
          dx: { type: 'number', description: 'Horizontal shift in document points.' },
          dy: { type: 'number', description: 'Vertical shift in document points.' },
          at: POINT,
          dry_run: DRY,
        },
        required: ['file', 'index'],
      },
      async run({ file, index, dx, dy, at, dry_run = true }, exec) {
        const argv = ['move', file, '--index', String(index)]
        if (typeof dx === 'number') argv.push('--dx', String(dx))
        if (typeof dy === 'number') argv.push('--dy', String(dy))
        if (at) argv.push('--at', at)
        const data = await callBridge(writeArgs(argv, { dry_run }), { signal: exec?.signal, timeoutMs: 240000 })
        return compact({ summary: renderWrite(data, dry_run !== false), ok: data.ok, ...data })
      },
    }),
    tool({
      name: 'chemdraw_add_arrow',
      description: 'Add a reaction arrow between two points. Use it to assemble a reaction scheme from structures added with chemdraw_add. dry_run is the default.',
      parameters: {
        type: 'object',
        properties: {
          file: FILE,
          from: POINT,
          to: POINT,
          dry_run: DRY,
        },
        required: ['file', 'from', 'to'],
      },
      async run({ file, from, to, dry_run = true }, exec) {
        const argv = ['arrow', file, '--from', from, '--to', to]
        const data = await callBridge(writeArgs(argv, { dry_run }), { signal: exec?.signal, timeoutMs: 240000 })
        return compact({ summary: renderWrite(data, dry_run !== false), ok: data.ok, ...data })
      },
    }),
    tool({
      name: 'chemdraw_edit',
      description:
        'Apply a batch of edits to a ChemDraw file in one transaction: a JSON array of operations {op:"add"|"delete"|"replace"|"annotate"|"move"|"arrow"}. All operations are validated together, the result is re-read and verified, a .bak copy is kept, and dry_run is the default. "at" accepts [x,y] or "x,y".',
      parameters: {
        type: 'object',
        properties: {
          file: FILE,
          ops: {
            type: 'array',
            description: 'Operations, e.g. [{"op":"add","smiles":"CCO","label":"5","at":[200,300]},{"op":"delete","id":3}]',
            items: { type: 'object', additionalProperties: true },
          },
          dry_run: DRY,
        },
        required: ['file', 'ops'],
      },
      async run({ file, ops, dry_run = true }, exec) {
        const opsFile = path.join(scratch(), `ops-${randomUUID()}.json`)
        await writeFile(opsFile, JSON.stringify({ ops }), 'utf8')
        try {
          const argv = ['edit', file, '--ops', opsFile]
          const data = await callBridge(writeArgs(argv, { dry_run }), { signal: exec?.signal, timeoutMs: 300000 })
          return compact({ summary: renderWrite(data, dry_run !== false), ok: data.ok, ...data })
        } finally {
          await rm(opsFile, { force: true })
        }
      },
    }),
    tool({
      name: 'chemdraw_doctor',
      description:
        'Check the local ChemDraw installation: version, activation state, COM automation availability and a self-test conversion. Use when another chemdraw_* tool fails unexpectedly.',
      parameters: { type: 'object', properties: {}, additionalProperties: false },
      async run(_args, exec) {
        const data = await callBridge(['doctor'], { signal: exec?.signal, timeoutMs: 90000 })
        const lines = [
          '# ChemDraw bridge',
          `ChemDraw ${data.chemdraw_version ?? '?'} (${data.chemdraw_exe ?? '?'})`,
          `pre-existing instance: ${data.preexisting_instance} · ChemScript activated: ${data.chemscript_activated}`,
          `engine: ${data.chemscript_version ?? '?'}`,
          `caption patterns: ${data.caption_regex_ok ? 'ok' : 'BROKEN'} · label rule: ${data.label_rule ?? '?'}`,
          `self test: ${data.self_test_smiles ?? 'failed'} (MW ${data.self_test_mw ?? '?'})`,
        ]
        return compact({ summary: lines.join('\n'), ok: data.ok, ...data })
      },
    }),
  ]

  for (const definition of definitions) {
    ctx.effect(() => ctx.tools.register(definition))
  }
}
