import { createElement, type HTMLAttributes } from 'react'
import type { Icon, IconWeight } from '@phosphor-icons/react/dist/lib/types'
import { AlignCenterVerticalIcon } from '@phosphor-icons/react/dist/csr/AlignCenterVertical'
import { PulseIcon } from '@phosphor-icons/react/dist/csr/Pulse'
import { AirplaneIcon } from '@phosphor-icons/react/dist/csr/Airplane'
import { AirplaneTakeoffIcon } from '@phosphor-icons/react/dist/csr/AirplaneTakeoff'
import { AlarmIcon } from '@phosphor-icons/react/dist/csr/Alarm'
import { ArchiveIcon } from '@phosphor-icons/react/dist/csr/Archive'
import { ArrowBendUpLeftIcon } from '@phosphor-icons/react/dist/csr/ArrowBendUpLeft'
import { ArrowCircleRightIcon } from '@phosphor-icons/react/dist/csr/ArrowCircleRight'
import { ArrowCircleUpIcon } from '@phosphor-icons/react/dist/csr/ArrowCircleUp'
import { ArrowClockwiseIcon } from '@phosphor-icons/react/dist/csr/ArrowClockwise'
import { ArrowCounterClockwiseIcon } from '@phosphor-icons/react/dist/csr/ArrowCounterClockwise'
import { ArrowDownIcon } from '@phosphor-icons/react/dist/csr/ArrowDown'
import { ArrowLeftIcon } from '@phosphor-icons/react/dist/csr/ArrowLeft'
import { ArrowRightIcon } from '@phosphor-icons/react/dist/csr/ArrowRight'
import { ArrowSquareOutIcon } from '@phosphor-icons/react/dist/csr/ArrowSquareOut'
import { ArrowUpIcon } from '@phosphor-icons/react/dist/csr/ArrowUp'
import { ArrowUpRightIcon } from '@phosphor-icons/react/dist/csr/ArrowUpRight'
import { ArrowsInIcon } from '@phosphor-icons/react/dist/csr/ArrowsIn'
import { ArrowsLeftRightIcon } from '@phosphor-icons/react/dist/csr/ArrowsLeftRight'
import { ArrowsOutIcon } from '@phosphor-icons/react/dist/csr/ArrowsOut'
import { ArrowsOutCardinalIcon } from '@phosphor-icons/react/dist/csr/ArrowsOutCardinal'
import { SelectionIcon } from '@phosphor-icons/react/dist/csr/Selection'
import { AtIcon } from '@phosphor-icons/react/dist/csr/At'
import { BagIcon } from '@phosphor-icons/react/dist/csr/Bag'
import { BankIcon } from '@phosphor-icons/react/dist/csr/Bank'
import { BarcodeIcon } from '@phosphor-icons/react/dist/csr/Barcode'
import { BasketIcon } from '@phosphor-icons/react/dist/csr/Basket'
import { BellIcon } from '@phosphor-icons/react/dist/csr/Bell'
import { BezierCurveIcon } from '@phosphor-icons/react/dist/csr/BezierCurve'
import { BookOpenIcon } from '@phosphor-icons/react/dist/csr/BookOpen'
import { BookmarkIcon } from '@phosphor-icons/react/dist/csr/Bookmark'
import { BoundingBoxIcon } from '@phosphor-icons/react/dist/csr/BoundingBox'
import { BracketsCurlyIcon } from '@phosphor-icons/react/dist/csr/BracketsCurly'
import { BrainIcon } from '@phosphor-icons/react/dist/csr/Brain'
import { BriefcaseIcon } from '@phosphor-icons/react/dist/csr/Briefcase'
import { BrowserIcon } from '@phosphor-icons/react/dist/csr/Browser'
import { BugIcon } from '@phosphor-icons/react/dist/csr/Bug'
import { BuildingsIcon } from '@phosphor-icons/react/dist/csr/Buildings'
import { BusIcon } from '@phosphor-icons/react/dist/csr/Bus'
import { CalendarBlankIcon } from '@phosphor-icons/react/dist/csr/CalendarBlank'
import { CalendarCheckIcon } from '@phosphor-icons/react/dist/csr/CalendarCheck'
import { CalendarDotsIcon } from '@phosphor-icons/react/dist/csr/CalendarDots'
import { CalendarHeartIcon } from '@phosphor-icons/react/dist/csr/CalendarHeart'
import { CalendarPlusIcon } from '@phosphor-icons/react/dist/csr/CalendarPlus'
import { CalendarXIcon } from '@phosphor-icons/react/dist/csr/CalendarX'
import { CameraIcon } from '@phosphor-icons/react/dist/csr/Camera'
import { ChartBarIcon } from '@phosphor-icons/react/dist/csr/ChartBar'
import { ChartLineUpIcon } from '@phosphor-icons/react/dist/csr/ChartLineUp'
import { ChartPieIcon } from '@phosphor-icons/react/dist/csr/ChartPie'
import { ChatCenteredTextIcon } from '@phosphor-icons/react/dist/csr/ChatCenteredText'
import { ChatCircleDotsIcon } from '@phosphor-icons/react/dist/csr/ChatCircleDots'
import { ChatCircleIcon } from '@phosphor-icons/react/dist/csr/ChatCircle'
import { ChatDotsIcon } from '@phosphor-icons/react/dist/csr/ChatDots'
import { CheckCircleIcon } from '@phosphor-icons/react/dist/csr/CheckCircle'
import { CheckIcon } from '@phosphor-icons/react/dist/csr/Check'
import { ChecksIcon } from '@phosphor-icons/react/dist/csr/Checks'
import { CheckSquareIcon } from '@phosphor-icons/react/dist/csr/CheckSquare'
import { CircleHalfIcon } from '@phosphor-icons/react/dist/csr/CircleHalf'
import { CircleIcon } from '@phosphor-icons/react/dist/csr/Circle'
import { ClipboardIcon } from '@phosphor-icons/react/dist/csr/Clipboard'
import { ClipboardTextIcon } from '@phosphor-icons/react/dist/csr/ClipboardText'
import { ClockCounterClockwiseIcon } from '@phosphor-icons/react/dist/csr/ClockCounterClockwise'
import { ClockIcon } from '@phosphor-icons/react/dist/csr/Clock'
import { CloudArrowUpIcon } from '@phosphor-icons/react/dist/csr/CloudArrowUp'
import { CloudIcon } from '@phosphor-icons/react/dist/csr/Cloud'
import { CloudRainIcon } from '@phosphor-icons/react/dist/csr/CloudRain'
import { CloudSunIcon } from '@phosphor-icons/react/dist/csr/CloudSun'
import { CodeBlockIcon } from '@phosphor-icons/react/dist/csr/CodeBlock'
import { CodeIcon } from '@phosphor-icons/react/dist/csr/Code'
import { CoffeeIcon } from '@phosphor-icons/react/dist/csr/Coffee'
import { CoinIcon } from '@phosphor-icons/react/dist/csr/Coin'
import { ColumnsIcon } from '@phosphor-icons/react/dist/csr/Columns'
import { CommandIcon } from '@phosphor-icons/react/dist/csr/Command'
import { CompassIcon } from '@phosphor-icons/react/dist/csr/Compass'
import { CookieIcon } from '@phosphor-icons/react/dist/csr/Cookie'
import { CpuIcon } from '@phosphor-icons/react/dist/csr/Cpu'
import { CreditCardIcon } from '@phosphor-icons/react/dist/csr/CreditCard'
import { CubeIcon } from '@phosphor-icons/react/dist/csr/Cube'
import { CurrencyDollarIcon } from '@phosphor-icons/react/dist/csr/CurrencyDollar'
import { CursorClickIcon } from '@phosphor-icons/react/dist/csr/CursorClick'
import { CursorTextIcon } from '@phosphor-icons/react/dist/csr/CursorText'
import { DatabaseIcon } from '@phosphor-icons/react/dist/csr/Database'
import { DiamondIcon } from '@phosphor-icons/react/dist/csr/Diamond'
import { DoorOpenIcon } from '@phosphor-icons/react/dist/csr/DoorOpen'
import { DotIcon } from '@phosphor-icons/react/dist/csr/Dot'
import { DownloadSimpleIcon } from '@phosphor-icons/react/dist/csr/DownloadSimple'
import { DropHalfIcon } from '@phosphor-icons/react/dist/csr/DropHalf'
import { DropIcon } from '@phosphor-icons/react/dist/csr/Drop'
import { PresentationIcon } from '@phosphor-icons/react/dist/csr/Presentation'
import { EggIcon } from '@phosphor-icons/react/dist/csr/Egg'
import { EnvelopeIcon } from '@phosphor-icons/react/dist/csr/Envelope'
import { EnvelopeOpenIcon } from '@phosphor-icons/react/dist/csr/EnvelopeOpen'
import { EraserIcon } from '@phosphor-icons/react/dist/csr/Eraser'
import { EyeIcon } from '@phosphor-icons/react/dist/csr/Eye'
import { EyeSlashIcon } from '@phosphor-icons/react/dist/csr/EyeSlash'
import { FacebookLogoIcon } from '@phosphor-icons/react/dist/csr/FacebookLogo'
import { FileArchiveIcon } from '@phosphor-icons/react/dist/csr/FileArchive'
import { FileArrowUpIcon } from '@phosphor-icons/react/dist/csr/FileArrowUp'
import { FileCodeIcon } from '@phosphor-icons/react/dist/csr/FileCode'
import { FileCsvIcon } from '@phosphor-icons/react/dist/csr/FileCsv'
import { FileDocIcon } from '@phosphor-icons/react/dist/csr/FileDoc'
import { FileHtmlIcon } from '@phosphor-icons/react/dist/csr/FileHtml'
import { FileImageIcon } from '@phosphor-icons/react/dist/csr/FileImage'
import { FileLockIcon } from '@phosphor-icons/react/dist/csr/FileLock'
import { FileMdIcon } from '@phosphor-icons/react/dist/csr/FileMd'
import { FilePdfIcon } from '@phosphor-icons/react/dist/csr/FilePdf'
import { FilePlusIcon } from '@phosphor-icons/react/dist/csr/FilePlus'
import { FilePptIcon } from '@phosphor-icons/react/dist/csr/FilePpt'
import { FileTextIcon } from '@phosphor-icons/react/dist/csr/FileText'
import { FileXlsIcon } from '@phosphor-icons/react/dist/csr/FileXls'
import { FilesIcon } from '@phosphor-icons/react/dist/csr/Files'
import { FilmStripIcon } from '@phosphor-icons/react/dist/csr/FilmStrip'
import { FireIcon } from '@phosphor-icons/react/dist/csr/Fire'
import { FlagIcon } from '@phosphor-icons/react/dist/csr/Flag'
import { FlaskIcon } from '@phosphor-icons/react/dist/csr/Flask'
import { FloppyDiskIcon } from '@phosphor-icons/react/dist/csr/FloppyDisk'
import { FlowerIcon } from '@phosphor-icons/react/dist/csr/Flower'
import { FolderIcon } from '@phosphor-icons/react/dist/csr/Folder'
import { FolderOpenIcon } from '@phosphor-icons/react/dist/csr/FolderOpen'
import { FolderPlusIcon } from '@phosphor-icons/react/dist/csr/FolderPlus'
import { FunnelIcon } from '@phosphor-icons/react/dist/csr/Funnel'
import { GameControllerIcon } from '@phosphor-icons/react/dist/csr/GameController'
import { GasPumpIcon } from '@phosphor-icons/react/dist/csr/GasPump'
import { GaugeIcon } from '@phosphor-icons/react/dist/csr/Gauge'
import { GearIcon } from '@phosphor-icons/react/dist/csr/Gear'
import { GiftIcon } from '@phosphor-icons/react/dist/csr/Gift'
import { GitBranchIcon } from '@phosphor-icons/react/dist/csr/GitBranch'
import { GlobeHemisphereWestIcon } from '@phosphor-icons/react/dist/csr/GlobeHemisphereWest'
import { GoogleLogoIcon } from '@phosphor-icons/react/dist/csr/GoogleLogo'
import { GraduationCapIcon } from '@phosphor-icons/react/dist/csr/GraduationCap'
import { HandPointingIcon } from '@phosphor-icons/react/dist/csr/HandPointing'
import { HardDriveIcon } from '@phosphor-icons/react/dist/csr/HardDrive'
import { HashIcon } from '@phosphor-icons/react/dist/csr/Hash'
import { HeadsetIcon } from '@phosphor-icons/react/dist/csr/Headset'
import { HeartIcon } from '@phosphor-icons/react/dist/csr/Heart'
import { HeartbeatIcon } from '@phosphor-icons/react/dist/csr/Heartbeat'
import { HourglassIcon } from '@phosphor-icons/react/dist/csr/Hourglass'
import { HouseIcon } from '@phosphor-icons/react/dist/csr/House'
import { IdentificationBadgeIcon } from '@phosphor-icons/react/dist/csr/IdentificationBadge'
import { ImageIcon } from '@phosphor-icons/react/dist/csr/Image'
import { ImagesIcon } from '@phosphor-icons/react/dist/csr/Images'
import { TrayIcon } from '@phosphor-icons/react/dist/csr/Tray'
import { InfoIcon } from '@phosphor-icons/react/dist/csr/Info'
import { InstagramLogoIcon } from '@phosphor-icons/react/dist/csr/InstagramLogo'
import { IntersectIcon } from '@phosphor-icons/react/dist/csr/Intersect'
import { KeyIcon } from '@phosphor-icons/react/dist/csr/Key'
import { KeyboardIcon } from '@phosphor-icons/react/dist/csr/Keyboard'
import { KanbanIcon } from '@phosphor-icons/react/dist/csr/Kanban'
import { LightbulbIcon } from '@phosphor-icons/react/dist/csr/Lightbulb'
import { LayoutIcon } from '@phosphor-icons/react/dist/csr/Layout'
import { LightningIcon } from '@phosphor-icons/react/dist/csr/Lightning'
import { LinkIcon } from '@phosphor-icons/react/dist/csr/Link'
import { LinkedinLogoIcon } from '@phosphor-icons/react/dist/csr/LinkedinLogo'
import { ListBulletsIcon } from '@phosphor-icons/react/dist/csr/ListBullets'
import { ListChecksIcon } from '@phosphor-icons/react/dist/csr/ListChecks'
import { ListIcon } from '@phosphor-icons/react/dist/csr/List'
import { ListNumbersIcon } from '@phosphor-icons/react/dist/csr/ListNumbers'
import { LockIcon } from '@phosphor-icons/react/dist/csr/Lock'
import { LockOpenIcon } from '@phosphor-icons/react/dist/csr/LockOpen'
import { MagicWandIcon } from '@phosphor-icons/react/dist/csr/MagicWand'
import { MagnifyingGlassIcon } from '@phosphor-icons/react/dist/csr/MagnifyingGlass'
import { MagnifyingGlassPlusIcon } from '@phosphor-icons/react/dist/csr/MagnifyingGlassPlus'
import { MapPinIcon } from '@phosphor-icons/react/dist/csr/MapPin'
import { MapTrifoldIcon } from '@phosphor-icons/react/dist/csr/MapTrifold'
import { MedalIcon } from '@phosphor-icons/react/dist/csr/Medal'
import { MegaphoneIcon } from '@phosphor-icons/react/dist/csr/Megaphone'
import { MicrophoneIcon } from '@phosphor-icons/react/dist/csr/Microphone'
import { MicrosoftExcelLogoIcon } from '@phosphor-icons/react/dist/csr/MicrosoftExcelLogo'
import { MicrosoftOutlookLogoIcon } from '@phosphor-icons/react/dist/csr/MicrosoftOutlookLogo'
import { MicrosoftPowerpointLogoIcon } from '@phosphor-icons/react/dist/csr/MicrosoftPowerpointLogo'
import { MicrosoftTeamsLogoIcon } from '@phosphor-icons/react/dist/csr/MicrosoftTeamsLogo'
import { MicrosoftWordLogoIcon } from '@phosphor-icons/react/dist/csr/MicrosoftWordLogo'
import { MinusCircleIcon } from '@phosphor-icons/react/dist/csr/MinusCircle'
import { MinusIcon } from '@phosphor-icons/react/dist/csr/Minus'
import { MoonIcon } from '@phosphor-icons/react/dist/csr/Moon'
import { MusicNotesIcon } from '@phosphor-icons/react/dist/csr/MusicNotes'
import { NewspaperIcon } from '@phosphor-icons/react/dist/csr/Newspaper'
import { NoteIcon } from '@phosphor-icons/react/dist/csr/Note'
import { NotebookIcon } from '@phosphor-icons/react/dist/csr/Notebook'
import { NotepadIcon } from '@phosphor-icons/react/dist/csr/Notepad'
import { NumberCircleFourIcon } from '@phosphor-icons/react/dist/csr/NumberCircleFour'
import { NumberCircleOneIcon } from '@phosphor-icons/react/dist/csr/NumberCircleOne'
import { NumberCircleThreeIcon } from '@phosphor-icons/react/dist/csr/NumberCircleThree'
import { NumberCircleTwoIcon } from '@phosphor-icons/react/dist/csr/NumberCircleTwo'
import { NumberSquareOneIcon } from '@phosphor-icons/react/dist/csr/NumberSquareOne'
import { OctagonIcon } from '@phosphor-icons/react/dist/csr/Octagon'
import { PackageIcon } from '@phosphor-icons/react/dist/csr/Package'
import { PaintBrushIcon } from '@phosphor-icons/react/dist/csr/PaintBrush'
import { PaletteIcon } from '@phosphor-icons/react/dist/csr/Palette'
import { PaperPlaneTiltIcon } from '@phosphor-icons/react/dist/csr/PaperPlaneTilt'
import { PaperclipIcon } from '@phosphor-icons/react/dist/csr/Paperclip'
import { PauseIcon } from '@phosphor-icons/react/dist/csr/Pause'
import { PaypalLogoIcon } from '@phosphor-icons/react/dist/csr/PaypalLogo'
import { PencilIcon } from '@phosphor-icons/react/dist/csr/Pencil'
import { PhoneIcon } from '@phosphor-icons/react/dist/csr/Phone'
import { PillIcon } from '@phosphor-icons/react/dist/csr/Pill'
import { PlayCircleIcon } from '@phosphor-icons/react/dist/csr/PlayCircle'
import { PlayIcon } from '@phosphor-icons/react/dist/csr/Play'
import { PlugIcon } from '@phosphor-icons/react/dist/csr/Plug'
import { PlusCircleIcon } from '@phosphor-icons/react/dist/csr/PlusCircle'
import { PlusIcon } from '@phosphor-icons/react/dist/csr/Plus'
import { PlusMinusIcon } from '@phosphor-icons/react/dist/csr/PlusMinus'
import { PlusSquareIcon } from '@phosphor-icons/react/dist/csr/PlusSquare'
import { PrinterIcon } from '@phosphor-icons/react/dist/csr/Printer'
import { PuzzlePieceIcon } from '@phosphor-icons/react/dist/csr/PuzzlePiece'
import { QuestionIcon } from '@phosphor-icons/react/dist/csr/Question'
import { ReceiptIcon } from '@phosphor-icons/react/dist/csr/Receipt'
import { RecordIcon } from '@phosphor-icons/react/dist/csr/Record'
import { RecycleIcon } from '@phosphor-icons/react/dist/csr/Recycle'
import { RepeatIcon } from '@phosphor-icons/react/dist/csr/Repeat'
import { RobotIcon } from '@phosphor-icons/react/dist/csr/Robot'
import { RocketLaunchIcon } from '@phosphor-icons/react/dist/csr/RocketLaunch'
import { RulerIcon } from '@phosphor-icons/react/dist/csr/Ruler'
import { ScissorsIcon } from '@phosphor-icons/react/dist/csr/Scissors'
import { SealCheckIcon } from '@phosphor-icons/react/dist/csr/SealCheck'
import { SealQuestionIcon } from '@phosphor-icons/react/dist/csr/SealQuestion'
import { ShareNetworkIcon } from '@phosphor-icons/react/dist/csr/ShareNetwork'
import { ShieldCheckIcon } from '@phosphor-icons/react/dist/csr/ShieldCheck'
import { ShieldIcon } from '@phosphor-icons/react/dist/csr/Shield'
import { ShieldSlashIcon } from '@phosphor-icons/react/dist/csr/ShieldSlash'
import { ShieldWarningIcon } from '@phosphor-icons/react/dist/csr/ShieldWarning'
import { ShoppingBagIcon } from '@phosphor-icons/react/dist/csr/ShoppingBag'
import { ShoppingCartIcon } from '@phosphor-icons/react/dist/csr/ShoppingCart'
import { ShuffleIcon } from '@phosphor-icons/react/dist/csr/Shuffle'
import { SidebarIcon } from '@phosphor-icons/react/dist/csr/Sidebar'
import { SignOutIcon } from '@phosphor-icons/react/dist/csr/SignOut'
import { SignpostIcon } from '@phosphor-icons/react/dist/csr/Signpost'
import { SlidersHorizontalIcon } from '@phosphor-icons/react/dist/csr/SlidersHorizontal'
import { SmileyIcon } from '@phosphor-icons/react/dist/csr/Smiley'
import { SmileyMehIcon } from '@phosphor-icons/react/dist/csr/SmileyMeh'
import { SnowflakeIcon } from '@phosphor-icons/react/dist/csr/Snowflake'
import { SparkleIcon } from '@phosphor-icons/react/dist/csr/Sparkle'
import { SquareIcon } from '@phosphor-icons/react/dist/csr/Square'
import { SquaresFourIcon } from '@phosphor-icons/react/dist/csr/SquaresFour'
import { StackIcon } from '@phosphor-icons/react/dist/csr/Stack'
import { StarIcon } from '@phosphor-icons/react/dist/csr/Star'
import { StopCircleIcon } from '@phosphor-icons/react/dist/csr/StopCircle'
import { StopIcon } from '@phosphor-icons/react/dist/csr/Stop'
import { TimerIcon } from '@phosphor-icons/react/dist/csr/Timer'
import { StorefrontIcon } from '@phosphor-icons/react/dist/csr/Storefront'
import { SunIcon } from '@phosphor-icons/react/dist/csr/Sun'
import { TableIcon } from '@phosphor-icons/react/dist/csr/Table'
import { TagIcon } from '@phosphor-icons/react/dist/csr/Tag'
import { TargetIcon } from '@phosphor-icons/react/dist/csr/Target'
import { TerminalIcon } from '@phosphor-icons/react/dist/csr/Terminal'
import { TextAlignCenterIcon } from '@phosphor-icons/react/dist/csr/TextAlignCenter'
import { TextAlignLeftIcon } from '@phosphor-icons/react/dist/csr/TextAlignLeft'
import { TextAlignRightIcon } from '@phosphor-icons/react/dist/csr/TextAlignRight'
import { TextTIcon } from '@phosphor-icons/react/dist/csr/TextT'
import { TiktokLogoIcon } from '@phosphor-icons/react/dist/csr/TiktokLogo'
import { ToggleLeftIcon } from '@phosphor-icons/react/dist/csr/ToggleLeft'
import { ToolboxIcon } from '@phosphor-icons/react/dist/csr/Toolbox'
import { TrashIcon } from '@phosphor-icons/react/dist/csr/Trash'
import { ThumbsUpIcon } from '@phosphor-icons/react/dist/csr/ThumbsUp'
import { TreeStructureIcon } from '@phosphor-icons/react/dist/csr/TreeStructure'
import { TrendUpIcon } from '@phosphor-icons/react/dist/csr/TrendUp'
import { TriangleIcon } from '@phosphor-icons/react/dist/csr/Triangle'
import { TrophyIcon } from '@phosphor-icons/react/dist/csr/Trophy'
import { UserCheckIcon } from '@phosphor-icons/react/dist/csr/UserCheck'
import { UserCircleIcon } from '@phosphor-icons/react/dist/csr/UserCircle'
import { UserFocusIcon } from '@phosphor-icons/react/dist/csr/UserFocus'
import { UserGearIcon } from '@phosphor-icons/react/dist/csr/UserGear'
import { UserIcon } from '@phosphor-icons/react/dist/csr/User'
import { UserMinusIcon } from '@phosphor-icons/react/dist/csr/UserMinus'
import { UserPlusIcon } from '@phosphor-icons/react/dist/csr/UserPlus'
import { UsersIcon } from '@phosphor-icons/react/dist/csr/Users'
import { UploadSimpleIcon } from '@phosphor-icons/react/dist/csr/UploadSimple'
import { VideoCameraIcon } from '@phosphor-icons/react/dist/csr/VideoCamera'
import { WarningIcon } from '@phosphor-icons/react/dist/csr/Warning'
import { WatchIcon } from '@phosphor-icons/react/dist/csr/Watch'
import { WaveformIcon } from '@phosphor-icons/react/dist/csr/Waveform'
import { WavesIcon } from '@phosphor-icons/react/dist/csr/Waves'
import { WrenchIcon } from '@phosphor-icons/react/dist/csr/Wrench'
import { XCircleIcon } from '@phosphor-icons/react/dist/csr/XCircle'
import { XIcon } from '@phosphor-icons/react/dist/csr/X'
import { XLogoIcon } from '@phosphor-icons/react/dist/csr/XLogo'
import { CaretDownIcon } from '@phosphor-icons/react/dist/csr/CaretDown'
import { CaretLeftIcon } from '@phosphor-icons/react/dist/csr/CaretLeft'
import { CaretRightIcon } from '@phosphor-icons/react/dist/csr/CaretRight'
import { CaretUpIcon } from '@phosphor-icons/react/dist/csr/CaretUp'
import { DotsThreeIcon } from '@phosphor-icons/react/dist/csr/DotsThree'

type PhosphorIconProps = HTMLAttributes<HTMLElement> & {
  weight?: IconWeight
}

const groups: Array<[Icon, string[]]> = [
  [NumberCircleOneIcon, ['1-circle']], [NumberCircleTwoIcon, ['2-circle']],
  [NumberCircleThreeIcon, ['3-circle']], [NumberCircleFourIcon, ['4-circle']],
  [NumberSquareOneIcon, ['123']], [PulseIcon, ['activity']],
  [AirplaneIcon, ['airplane']], [AirplaneTakeoffIcon, ['airplane-engines']],
  [AlarmIcon, ['alarm']], [ArchiveIcon, ['archive']],
  [ArrowClockwiseIcon, ['arrow-clockwise']], [ArrowCounterClockwiseIcon, ['arrow-counterclockwise']],
  [ArrowLeftIcon, ['arrow-left']], [ArrowsLeftRightIcon, ['arrow-left-right']],
  [RepeatIcon, ['arrow-repeat']], [ArrowRightIcon, ['arrow-right']],
  [ArrowCircleRightIcon, ['arrow-right-circle']], [ArrowSquareOutIcon, ['arrow-right-square', 'box-arrow-up-right', 'box-arrow-in-down-right']],
  [ArrowUpIcon, ['arrow-up']], [ArrowCircleUpIcon, ['arrow-up-circle']],
  [ArrowUpRightIcon, ['arrow-up-right']], [ArrowDownIcon, ['arrow-down']],
  [ArrowsInIcon, ['arrows-collapse']], [ArrowsOutIcon, ['arrows-expand']],
  [ArrowsOutCardinalIcon, ['arrows-move']], [SelectionIcon, ['aspect-ratio']],
  [AtIcon, ['at']], [MedalIcon, ['award']], [BagIcon, ['bag']],
  [ShoppingBagIcon, ['bag-check', 'handbag']], [BankIcon, ['bank']],
  [ChartBarIcon, ['bar-chart', 'bar-chart-fill', 'bar-chart-line', 'bar-chart-steps']],
  [BasketIcon, ['basket', 'basket3']], [FlaskIcon, ['beaker']], [BellIcon, ['bell']],
  [BezierCurveIcon, ['bezier2']], [TextAlignLeftIcon, ['body-text', 'text-paragraph']],
  [BookOpenIcon, ['book']], [BookmarkIcon, ['bookmark', 'bookmark-plus-fill', 'bookmark-star']],
  [BoundingBoxIcon, ['bounding-box']], [CubeIcon, ['box']],
  [DownloadSimpleIcon, ['box-arrow-in-down', 'download']], [SignOutIcon, ['box-arrow-right']],
  [PackageIcon, ['box-seam']], [StackIcon, ['boxes', 'layers']],
  [BracketsCurlyIcon, ['braces', 'braces-asterisk']], [BrainIcon, ['brain']],
  [BriefcaseIcon, ['briefcase']], [SunIcon, ['brightness-high', 'sun']],
  [BrowserIcon, ['browser-chrome', 'display']], [PaintBrushIcon, ['brush']], [BugIcon, ['bug']],
  [BuildingsIcon, ['building', 'buildings']], [TargetIcon, ['bullseye']], [BusIcon, ['bus-front']],
  [CalendarBlankIcon, ['calendar', 'calendar3']], [CalendarCheckIcon, ['calendar-check', 'calendar2-check']],
  [CalendarDotsIcon, ['calendar-event', 'calendar-range', 'calendar2-week']],
  [CalendarPlusIcon, ['calendar-plus']], [CalendarXIcon, ['calendar-x']], [CalendarHeartIcon, ['calendar2-heart']],
  [CameraIcon, ['camera', 'camera-fill']], [VideoCameraIcon, ['camera-video', 'person-video2']],
  [PillIcon, ['capsule', 'capsule-pill']], [ShoppingCartIcon, ['cart', 'cart-plus', 'cart3']],
  [CurrencyDollarIcon, ['cash-coin', 'currency-dollar']],
  [ChatCircleIcon, ['chat', 'chat-heart', 'chat-heart-fill']],
  [ChatDotsIcon, ['chat-dots', 'chat-dots-fill']], [ChatCircleDotsIcon, ['chat-left-quote', 'chat-quote', 'chat-right-quote']],
  [ChatCenteredTextIcon, ['chat-square-text', 'chat-text']], [CheckIcon, ['check', 'check-lg', 'check2']],
  [ChecksIcon, ['check-all', 'check2-all']], [CheckCircleIcon, ['check-circle', 'check-circle-fill', 'check2-circle']],
  [CheckSquareIcon, ['check-square', 'check2-square']], [CaretDownIcon, ['chevron-down']],
  [CaretLeftIcon, ['chevron-left']], [CaretRightIcon, ['chevron-right']], [CaretUpIcon, ['chevron-up']],
  [CircleIcon, ['circle', 'circle-fill']], [CircleHalfIcon, ['circle-half']], [ClipboardIcon, ['clipboard', 'clipboard-fill', 'clipboard-plus', 'clipboard2']],
  [ClipboardTextIcon, ['clipboard-check', 'clipboard-data', 'clipboard-heart']], [ClockIcon, ['clock']],
  [ClockCounterClockwiseIcon, ['clock-history']], [CloudIcon, ['cloud', 'cloud-fill']],
  [CloudArrowUpIcon, ['cloud-arrow-up', 'cloud-upload']], [CloudRainIcon, ['cloud-rain']], [CloudSunIcon, ['cloud-sun']],
  [CodeIcon, ['code', 'code-slash']], [CodeBlockIcon, ['code-square']], [CoinIcon, ['coin']],
  [SquaresFourIcon, ['collection', 'collection-fill', 'grid', 'grid-1x2', 'grid-2x2-gap', 'grid-3x3', 'grid-3x3-gap', 'grid-3x3-gap-fill']],
  [PlayCircleIcon, ['collection-play', 'play-circle', 'play-circle-fill']], [ColumnsIcon, ['columns-gap', 'layout-split', 'layout-three-columns']],
  [CommandIcon, ['command']], [CompassIcon, ['compass']], [GameControllerIcon, ['controller']], [CookieIcon, ['cookie']],
  [CpuIcon, ['cpu']], [CoffeeIcon, ['cup', 'cup-hot', 'cup-straw']], [CursorClickIcon, ['cursor']], [CursorTextIcon, ['cursor-text', 'input-cursor-text']],
  [MinusIcon, ['dash', 'dash-lg']], [MinusCircleIcon, ['dash-circle']], [DatabaseIcon, ['database']],
  [TreeStructureIcon, ['diagram-2', 'diagram-3', 'diagram-3-fill', 'node-plus']],
  [AlignCenterVerticalIcon, ['distribute-vertical']], [SidebarIcon, ['layout-sidebar']],
  [LayoutIcon, ['layout-text-window', 'layout-text-window-reverse']],
  [DoorOpenIcon, ['door-open']], [DotIcon, ['dot']], [DropIcon, ['droplet', 'droplet-fill', 'water']], [DropHalfIcon, ['droplet-half']],
  [PresentationIcon, ['easel', 'easel2']], [EggIcon, ['egg', 'egg-fried']], [SmileyMehIcon, ['emoji-neutral']], [SmileyIcon, ['emoji-smile']],
  [EnvelopeIcon, ['envelope', 'envelope-check', 'envelope-fill', 'envelope-paper']], [EnvelopeOpenIcon, ['envelope-open']], [EraserIcon, ['eraser-fill']],
  [InfoIcon, ['exclamation-circle', 'exclamation-circle-fill', 'info-circle', 'info-circle-fill']],
  [WarningIcon, ['exclamation-triangle', 'exclamation-triangle-fill']], [EyeIcon, ['eye']], [EyeSlashIcon, ['eye-slash']],
  [FacebookLogoIcon, ['facebook']], [FileTextIcon, ['file-diff', 'file-earmark', 'file-earmark-check', 'file-earmark-medical', 'file-earmark-richtext', 'file-earmark-ruled', 'file-earmark-text', 'file-text', 'file-text-fill']],
  [FileArrowUpIcon, ['file-earmark-arrow-up']], [FileCodeIcon, ['file-earmark-code']], [FilePlusIcon, ['file-earmark-plus']], [FileXlsIcon, ['file-earmark-excel', 'file-earmark-spreadsheet']],
  [FileImageIcon, ['file-earmark-image']], [FileLockIcon, ['file-earmark-lock']], [MusicNotesIcon, ['file-earmark-music', 'music-note-beamed']],
  [FilePdfIcon, ['file-earmark-pdf', 'filetype-pdf']], [FilePptIcon, ['file-earmark-ppt', 'file-earmark-slides']], [FileArchiveIcon, ['file-earmark-zip']],
  [FileDocIcon, ['file-earmark-word']], [FilesIcon, ['files']], [FileCsvIcon, ['filetype-csv']], [FileHtmlIcon, ['filetype-html']],
  [CodeBlockIcon, ['filetype-json']], [FileMdIcon, ['filetype-md', 'markdown']], [FilmStripIcon, ['film']], [FunnelIcon, ['filter', 'funnel']],
  [FireIcon, ['fire']], [FlagIcon, ['flag']], [FloppyDiskIcon, ['floppy', 'save']], [FlowerIcon, ['flower1', 'flower2', 'flower3']],
  [FolderPlusIcon, ['folder-plus']], [FolderIcon, ['folder-symlink', 'folder2']], [FolderOpenIcon, ['folder2-open']], [TextTIcon, ['fonts', 'type']],
  [GasPumpIcon, ['fuel-pump']], [GearIcon, ['gear']], [DiamondIcon, ['gem']], [MapPinIcon, ['geo-alt']], [GiftIcon, ['gift']],
  [GitBranchIcon, ['git']], [GlobeHemisphereWestIcon, ['globe', 'globe2']], [GoogleLogoIcon, ['google']], [InstagramLogoIcon, ['instagram']], [IntersectIcon, ['intersect']],
  [TrendUpIcon, ['graph-up', 'graph-up-arrow']], [HandPointingIcon, ['hand-index', 'hand-index-thumb']], [ThumbsUpIcon, ['hand-thumbs-up']],
  [HashIcon, ['hash']], [HardDriveIcon, ['hdd', 'hdd-network']], [HeadsetIcon, ['headset']], [HeartIcon, ['heart', 'heart-fill']], [HeartbeatIcon, ['heart-pulse']],
  [HourglassIcon, ['hourglass', 'hourglass-split']], [HouseIcon, ['house', 'house-door', 'house-heart']], [ImageIcon, ['image']], [ImagesIcon, ['images']],
  [TrayIcon, ['inbox']], [NotepadIcon, ['journal-bookmark', 'journal-check', 'journal-plus', 'journal-richtext', 'journal-text', 'journals']],
  [KanbanIcon, ['kanban']], [KeyIcon, ['key', 'key-fill']], [KeyboardIcon, ['keyboard']], [LightbulbIcon, ['lightbulb', 'lightbulb-fill']],
  [LightningIcon, ['lightning', 'lightning-charge', 'lightning-charge-fill', 'lightning-fill']], [LinkIcon, ['link', 'link-45deg']], [LinkedinLogoIcon, ['linkedin']],
  [ListIcon, ['list']], [ListChecksIcon, ['list-check']], [ListNumbersIcon, ['list-ol']], [ListBulletsIcon, ['list-ul']], [LockIcon, ['lock', 'shield-lock', 'shield-lock-fill']],
  [MagicWandIcon, ['magic']], [MapTrifoldIcon, ['map']], [MegaphoneIcon, ['megaphone']], [MicrophoneIcon, ['mic', 'mic-fill']],
  [MicrosoftOutlookLogoIcon, ['microsoft']], [MicrosoftTeamsLogoIcon, ['microsoft-teams']], [MoonIcon, ['moon']], [GraduationCapIcon, ['mortarboard']],
  [NewspaperIcon, ['newspaper']], [PaletteIcon, ['palette', 'palette2']], [PaperclipIcon, ['paperclip']],
  [SealCheckIcon, ['patch-check', 'patch-check-fill']], [WarningIcon, ['patch-exclamation-fill']], [SealQuestionIcon, ['patch-question']], [PauseIcon, ['pause']],
  [PaypalLogoIcon, ['paypal']], [PencilIcon, ['pencil', 'pencil-square']], [UsersIcon, ['people', 'people-fill']], [UserIcon, ['person', 'person-fill']],
  [UserFocusIcon, ['person-arms-up', 'person-bounding-box']], [IdentificationBadgeIcon, ['person-badge', 'person-vcard']], [UserCheckIcon, ['person-check', 'person-check-fill']],
  [UserCircleIcon, ['person-circle']], [UserMinusIcon, ['person-dash']], [UserGearIcon, ['person-gear']], [UserPlusIcon, ['person-plus']], [PhoneIcon, ['phone']],
  [ChartPieIcon, ['pie-chart']], [PlayIcon, ['play', 'play-fill']], [PlugIcon, ['plug']], [PlusIcon, ['plus', 'plus-lg']], [PlusCircleIcon, ['plus-circle']],
  [PlusMinusIcon, ['plus-slash-minus']], [PlusSquareIcon, ['plus-square']], [PrinterIcon, ['printer']], [PuzzlePieceIcon, ['puzzle', 'puzzle-fill']],
  [QuestionIcon, ['question-circle', 'question-lg']], [ReceiptIcon, ['receipt', 'receipt-cutoff']], [RecordIcon, ['record-circle']], [RecycleIcon, ['recycle']],
  [ArrowBendUpLeftIcon, ['reply']], [RobotIcon, ['robot']], [RocketLaunchIcon, ['rocket-takeoff']], [RulerIcon, ['rulers']], [ScissorsIcon, ['scissors']],
  [MagnifyingGlassIcon, ['search']], [PaperPlaneTiltIcon, ['send', 'send-fill']], [ShareNetworkIcon, ['share']], [ShieldIcon, ['shield']],
  [ShieldCheckIcon, ['shield-check']], [ShieldWarningIcon, ['shield-exclamation']], [ShieldSlashIcon, ['shield-slash']], [StorefrontIcon, ['shop']], [ShuffleIcon, ['shuffle']],
  [SignpostIcon, ['signpost-2', 'signpost-split']], [CircleIcon, ['slash-circle']], [SquareIcon, ['slash-square', 'square']], [SlidersHorizontalIcon, ['sliders']],
  [WatchIcon, ['smartwatch']], [SnowflakeIcon, ['snow']], [WaveformIcon, ['soundwave']], [GaugeIcon, ['speedometer2']], [StarIcon, ['star', 'star-fill']],
  [SparkleIcon, ['stars']], [NoteIcon, ['sticky']], [StopCircleIcon, ['stop-circle', 'stop-circle-fill']], [StopIcon, ['stop-fill']], [TimerIcon, ['stopwatch']],
  [TrophyIcon, ['suit-club']], [TableIcon, ['table']], [TagIcon, ['tag', 'tags']], [TerminalIcon, ['terminal']],
  [TextAlignCenterIcon, ['text-center']], [TextAlignLeftIcon, ['text-left']], [TextAlignRightIcon, ['text-right']], [DotsThreeIcon, ['three-dots']], [TiktokLogoIcon, ['tiktok']],
  [ToggleLeftIcon, ['toggles']], [ToolboxIcon, ['tools']], [TrashIcon, ['trash', 'trash3']], [TriangleIcon, ['triangle']], [XLogoIcon, ['twitter-x']],
  [SquaresFourIcon, ['union']], [LockOpenIcon, ['unlock']], [BarcodeIcon, ['upc-scan']], [UploadSimpleIcon, ['upload']], [WavesIcon, ['water']], [WrenchIcon, ['wrench']],
  [XIcon, ['x', 'x-lg']], [XCircleIcon, ['x-circle', 'x-circle-fill', 'x-octagon', 'x-octagon-fill']], [MagnifyingGlassPlusIcon, ['zoom-in']],
  [MicrosoftExcelLogoIcon, ['file-earmark-excel']], [MicrosoftPowerpointLogoIcon, ['file-earmark-ppt']], [MicrosoftWordLogoIcon, ['file-earmark-word']],
  [CreditCardIcon, ['card']], [NotebookIcon, ['journal']], [ChartLineUpIcon, ['graph-up-arrow']], [OctagonIcon, ['octagon']],

  // Names that were being used across the app but had no mapping, so they
  // silently rendered the question-mark fallback. Each reuses an icon that
  // is already imported here, so this costs nothing in bundle size.
  [PauseIcon, ['pause-fill']],
  [PlayCircleIcon, ['pause-circle']],
  [ChatCenteredTextIcon, ['chat-left-text']],
  [ShieldCheckIcon, ['shield-plus']],
  [GameControllerIcon, ['dice-5']],
  [MapPinIcon, ['pin-angle', 'pin-angle-fill']],
  [VideoCameraIcon, ['camera-video-fill']],
  [BrowserIcon, ['window']],
  [MicrophoneIcon, ['microphone-mute', 'mic-mute-fill']],
  [FolderOpenIcon, ['folder-check']],
  [FileTextIcon, ['file-earmark-minus']],
  [MoonIcon, ['moon-stars']],
  [UserIcon, ['person-walking']],
  [HardDriveIcon, ['usb-drive']],
  [UserMinusIcon, ['person-x']],
  [SquaresFourIcon, ['windows']],
]

const iconMap = new Map<string, Icon>()
for (const [IconComponent, names] of groups) {
  for (const name of names) iconMap.set(`bi-${name}`, IconComponent)
}

/**
 * Transitional semantic adapter for the app's former Bootstrap icon names.
 * Every glyph is rendered by Phosphor; legacy class tokens are retained only so
 * existing layout selectors and data-driven icon configuration keep working.
 */
export default function PhosphorIcon({ className = '', weight, ...props }: PhosphorIconProps) {
  const iconName = className.split(/\s+/).find((token) => token.startsWith('bi-'))
  const mapped = iconName ? iconMap.get(iconName) : undefined
  const IconComponent = mapped || QuestionIcon
  const resolvedWeight = weight ?? (iconName?.endsWith('-fill') ? 'fill' : 'regular')

  // An unmapped name silently renders a question mark, which looks like a
  // broken button rather than a missing icon — and it ships to production
  // looking deliberate. Say so loudly in development instead.
  if (import.meta.env.DEV && iconName && !mapped) {
    console.warn(
      `[PhosphorIcon] "${iconName}" is not in the icon map, so a "?" is being rendered. ` +
      `Add it to the groups table in PhosphorIcon.tsx or use a mapped name.`,
    )
  }

  return (
    <i className={`phosphor-icon ${className}`.trim()} {...props}>
      {createElement(IconComponent, { 'aria-hidden': true, focusable: false, size: '1em', weight: resolvedWeight })}
    </i>
  )
}
