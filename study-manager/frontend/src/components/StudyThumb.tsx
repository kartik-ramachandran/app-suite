// Miniature schematic previews for the Study Studio style gallery — one per
// renderer archetype, so users see the *shape* of what they'll get before
// generating. Pure inline SVG, no data needed.

const INK = '#94a3b8'
const ACCENT = '#d62976'
const ACCENT2 = '#833ab4'
const SOFT = '#e2e8f0'

export default function StyleThumb({ archetype }: { archetype: string }) {
  const svg = (children: React.ReactNode) => (
    <svg className="ss-thumb" viewBox="0 0 96 56" aria-hidden="true" focusable="false">{children}</svg>
  )

  switch (archetype) {
    case 'board':
      return svg(<>
        <rect x="8" y="8" width="24" height="22" rx="2" fill="#fef08a" transform="rotate(-3 20 19)" />
        <rect x="37" y="10" width="24" height="22" rx="2" fill="#fbcfe8" />
        <rect x="66" y="7" width="24" height="22" rx="2" fill="#bfdbfe" transform="rotate(2 78 18)" />
        <rect x="22" y="33" width="24" height="19" rx="2" fill="#bbf7d0" transform="rotate(2 34 42)" />
        <rect x="52" y="34" width="24" height="19" rx="2" fill="#fed7aa" transform="rotate(-2 64 43)" />
      </>)
    case 'sheet':
      return svg(<>
        <rect x="7" y="7" width="39" height="20" rx="3" fill="none" stroke={SOFT} strokeWidth="2" />
        <rect x="50" y="7" width="39" height="28" rx="3" fill="none" stroke={SOFT} strokeWidth="2" />
        <rect x="7" y="31" width="39" height="18" rx="3" fill="none" stroke={SOFT} strokeWidth="2" />
        <rect x="50" y="39" width="39" height="10" rx="3" fill="none" stroke={SOFT} strokeWidth="2" />
        <rect x="11" y="11" width="22" height="3" rx="1.5" fill={ACCENT} />
        <rect x="54" y="11" width="22" height="3" rx="1.5" fill={ACCENT2} />
        <rect x="11" y="18" width="30" height="2.5" rx="1.25" fill={INK} opacity=".55" />
        <rect x="54" y="18" width="30" height="2.5" rx="1.25" fill={INK} opacity=".55" />
        <rect x="54" y="24" width="26" height="2.5" rx="1.25" fill={INK} opacity=".55" />
        <rect x="11" y="37" width="28" height="2.5" rx="1.25" fill={INK} opacity=".55" />
      </>)
    case 'diagram':
      return svg(<>
        <rect x="34" y="6" width="28" height="12" rx="3" fill={ACCENT2} opacity=".85" />
        <rect x="10" y="36" width="26" height="12" rx="3" fill="none" stroke={ACCENT} strokeWidth="2" />
        <rect x="60" y="36" width="26" height="12" rx="3" fill="none" stroke={ACCENT} strokeWidth="2" />
        <path d="M44 18 L28 34 M52 18 L68 34" stroke={INK} strokeWidth="2" fill="none" />
        <path d="M28 34 l-3 -5 M28 34 l6 -1 M68 34 l3 -5 M68 34 l-6 -1" stroke={INK} strokeWidth="2" fill="none" />
      </>)
    case 'mindmap':
      return svg(<>
        <ellipse cx="48" cy="28" rx="14" ry="8" fill="#1e293b" />
        <path d="M40 23 Q26 15 16 12 M56 23 Q70 14 80 11 M40 33 Q26 41 14 45 M56 33 Q70 42 82 45" stroke={ACCENT} strokeWidth="2.5" fill="none" strokeLinecap="round" />
        <circle cx="16" cy="12" r="3.5" fill="#f59e0b" />
        <circle cx="80" cy="11" r="3.5" fill="#14b8a6" />
        <circle cx="14" cy="45" r="3.5" fill="#8b5cf6" />
        <circle cx="82" cy="45" r="3.5" fill="#ec4899" />
      </>)
    case 'timeline':
      return svg(<>
        <line x1="20" y1="6" x2="20" y2="50" stroke={ACCENT} strokeWidth="2.5" />
        <circle cx="20" cy="12" r="4" fill={ACCENT} />
        <circle cx="20" cy="28" r="4" fill={ACCENT2} />
        <circle cx="20" cy="44" r="4" fill={ACCENT} />
        <rect x="30" y="9" width="42" height="3" rx="1.5" fill={INK} opacity=".65" />
        <rect x="30" y="25" width="52" height="3" rx="1.5" fill={INK} opacity=".65" />
        <rect x="30" y="41" width="36" height="3" rx="1.5" fill={INK} opacity=".65" />
      </>)
    case 'venn':
      return svg(<>
        <circle cx="38" cy="28" r="18" fill={ACCENT} opacity=".28" stroke={ACCENT} strokeWidth="2" />
        <circle cx="58" cy="28" r="18" fill={ACCENT2} opacity=".22" stroke={ACCENT2} strokeWidth="2" />
      </>)
    case 'matrix':
      return svg(<>
        <rect x="8" y="8" width="80" height="40" rx="3" fill="none" stroke={SOFT} strokeWidth="2" />
        <rect x="8" y="8" width="80" height="11" rx="3" fill={ACCENT2} opacity=".8" />
        <line x1="35" y1="8" x2="35" y2="48" stroke={SOFT} strokeWidth="2" />
        <line x1="62" y1="8" x2="62" y2="48" stroke={SOFT} strokeWidth="2" />
        <line x1="8" y1="33" x2="88" y2="33" stroke={SOFT} strokeWidth="2" />
      </>)
    case 'cards':
      return svg(<>
        <rect x="14" y="12" width="34" height="34" rx="4" fill="none" stroke={ACCENT} strokeWidth="2" transform="rotate(-4 31 29)" />
        <rect x="48" y="10" width="34" height="34" rx="4" fill="#fdf2f8" stroke={ACCENT} strokeWidth="2" transform="rotate(3 65 27)" />
        <rect x="56" y="20" width="18" height="3" rx="1.5" fill={ACCENT} transform="rotate(3 65 27)" />
        <rect x="56" y="27" width="14" height="2.5" rx="1.25" fill={INK} opacity=".6" transform="rotate(3 65 27)" />
      </>)
    case 'worksheet':
      return svg(<>
        <rect x="10" y="8" width="10" height="10" rx="2" fill="none" stroke={ACCENT} strokeWidth="2" />
        <path d="M12.5 13 l2.5 3 l4.5 -5.5" stroke="#16a34a" strokeWidth="2" fill="none" />
        <rect x="26" y="11" width="58" height="3" rx="1.5" fill={INK} opacity=".65" />
        <rect x="10" y="24" width="10" height="10" rx="2" fill="none" stroke={ACCENT} strokeWidth="2" />
        <rect x="26" y="27" width="48" height="3" rx="1.5" fill={INK} opacity=".65" />
        <rect x="10" y="40" width="10" height="10" rx="2" fill="none" stroke={ACCENT} strokeWidth="2" />
        <rect x="26" y="43" width="54" height="3" rx="1.5" fill={INK} opacity=".65" />
      </>)
    default: // notes
      return svg(<>
        <rect x="10" y="8" width="52" height="4" rx="2" fill={ACCENT} />
        <rect x="10" y="17" width="76" height="3" rx="1.5" fill={INK} opacity=".6" />
        <rect x="10" y="24" width="68" height="3" rx="1.5" fill={INK} opacity=".6" />
        <rect x="10" y="31" width="74" height="3" rx="1.5" fill="#fef08a" />
        <rect x="10" y="31" width="74" height="3" rx="1.5" fill={INK} opacity=".55" />
        <rect x="10" y="38" width="60" height="3" rx="1.5" fill={INK} opacity=".6" />
        <rect x="10" y="45" width="44" height="3" rx="1.5" fill={INK} opacity=".6" />
      </>)
  }
}
