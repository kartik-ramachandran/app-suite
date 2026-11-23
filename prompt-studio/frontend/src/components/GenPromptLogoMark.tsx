import { useId } from 'react'

type GenPromptLogoMarkProps = {
  className?: string
  title?: string
}

function GenPromptLogoMark({ className, title }: GenPromptLogoMarkProps) {
  const gradientId = useId()

  return (
    <svg viewBox="0 0 100 100" className={className} role={title ? 'img' : 'presentation'} aria-label={title}>
      <defs>
        <linearGradient id={gradientId} x1="0%" y1="0%" x2="100%" y2="100%">
          <stop offset="0%" stopColor="#f17855" />
          <stop offset="48%" stopColor="#df5439" />
          <stop offset="100%" stopColor="#a93727" />
        </linearGradient>
      </defs>
      {/* Brand gradient tile */}
      <rect x="3" y="3" width="94" height="94" rx="20" fill={`url(#${gradientId})`} />
      {/* Clean speech bubble = prompting */}
      <path
        d="M28 32 C28 27, 33 23, 38 23 L62 23 C67 23, 72 27, 72 32 L72 48 C72 53, 67 57, 62 57 L46 57 L36 65 L36 57 C31 57, 28 53, 28 48 Z"
        fill="#ffffff"
      />
      {/* Single sparkle = AI generation */}
      <path
        d="M50 30 L52.7 37.3 L60 40 L52.7 42.7 L50 50 L47.3 42.7 L40 40 L47.3 37.3 Z"
        fill={`url(#${gradientId})`}
      />
    </svg>
  )
}

export default GenPromptLogoMark
