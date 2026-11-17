{
  "target": "数据密集型运维管理后台：左树右表，页面切换无过渡、卡片无圆角无层次、表格行无悬停反馈、空状态与加载态缺失、抽屉和弹窗生硬",
  "platform": "web",
  "summary": {
    "matchedRules": 14,
    "bySeverity": {
      "Critical": 3,
      "High": 4,
      "Medium": 7,
      "Low": 0
    },
    "checklistSections": 8
  },
  "priorityRules": [
    {
      "category": "Server",
      "issue": "Parallel Fetching",
      "platform": "React/Next.js",
      "description": "Restructure components to parallelize data fetching in RSC",
      "do": "Use component composition for parallel fetches",
      "dont": "Sequential fetches in parent component",
      "severity": "Critical",
      "source": "react",
      "score": 2.9676781560985903
    },
    {
      "category": "Touch",
      "issue": "Touch Target Size",
      "platform": "iOS/Android/React Native",
      "description": "Native targets use 44pt on iOS and 48dp on Android; web WCAG 2.2 has a separate 24 by 24 CSS px minimum with exceptions",
      "do": "Select 44pt for iOS and 48dp for Android at runtime; evaluate web targets separately against WCAG 2.5.8 and its exceptions",
      "dont": "Collapse iOS 44pt, Android 48dp, and web 24 CSS px into one cross-platform number",
      "severity": "Critical",
      "source": "web",
      "score": 1.2091334634440984
    },
    {
      "category": "Navigation",
      "issue": "Back Behavior",
      "platform": "iOS/Android/React Native",
      "description": "Back navigation should be predictable and preserve state",
      "do": "Use navigation.goBack and keep screen state",
      "dont": "Reset stack or exit app unexpectedly",
      "severity": "Critical",
      "source": "web",
      "score": 0.8038042699553973
    },
    {
      "category": "Bundle Size",
      "issue": "Conditional Loading",
      "platform": "React/Next.js",
      "description": "Load large data or modules only when a feature is activated",
      "do": "Dynamic import when feature enabled",
      "dont": "Import large modules unconditionally",
      "severity": "High",
      "source": "react",
      "score": 3.01503496476618
    },
    {
      "category": "Layout",
      "issue": "Content Jumping",
      "platform": "Web",
      "description": "Images badges validation text and skeleton replacements can shift nearby content when they update",
      "do": "Reserve appropriate space or keep async states in a stable content-driven container",
      "dont": "Insert compact text or media without a layout strategy",
      "severity": "High",
      "source": "ux",
      "score": 2.7944490774448365
    },
    {
      "category": "Feedback",
      "issue": "Loading Indicators",
      "platform": "iOS/Android/React Native",
      "description": "Show visible feedback during network operations",
      "do": "Use ActivityIndicator or skeleton for >300ms operations",
      "dont": "Leave button and screen frozen",
      "severity": "High",
      "source": "web",
      "score": 2.1065677529891187
    },
    {
      "category": "Accessibility",
      "issue": "Dragging Alternatives",
      "platform": "iOS/Android/React Native",
      "description": "React Native drag and reorder operations need a non-drag path selected for the active native runtime",
      "do": "Provide named Move up/down buttons or a position menu beside drag handles; route iOS and Android behavior through the runtime platform adapter",
      "dont": "Make drag, swipe, or a web-only pointer handler the only way to reorder native content",
      "severity": "High",
      "source": "web",
      "score": 0.7440334133962541
    },
    {
      "category": "Rendering",
      "issue": "Activity Component",
      "platform": "React/Next.js",
      "description": "Use Activity component to preserve state/DOM for toggled components",
      "do": "Use Activity for expensive toggle components",
      "dont": "Unmount/remount on visibility toggle",
      "severity": "Medium",
      "source": "react",
      "score": 3.4687294414452836
    },
    {
      "category": "Performance",
      "issue": "Font Loading",
      "platform": "Web",
      "description": "Web fonts can block rendering",
      "do": "Use font-display swap or optional",
      "dont": "Invisible text during font load",
      "severity": "Medium",
      "source": "ux",
      "score": 3.313374801117991
    },
    {
      "category": "Sustainability",
      "issue": "Auto-Play Video",
      "platform": "Web",
      "description": "Autoplaying media consumes data and creates motion barriers",
      "do": "Prefer click-to-play; provide pause and captions; stop off-screen and honor reduced motion",
      "dont": "Auto-play high-resolution loops without pause or captions",
      "severity": "Medium",
      "source": "ux",
      "score": 3.027524779844003
    },
    {
      "category": "Typography",
      "issue": "Font Loading",
      "platform": "Web",
      "description": "Fonts should load without layout shift",
      "do": "Reserve space with fallback font",
      "dont": "Layout shift when fonts load",
      "severity": "Medium",
      "source": "ux",
      "score": 2.803195700937079
    },
    {
      "category": "Feedback",
      "issue": "Empty States",
      "platform": "All",
      "description": "Guide users when no content exists",
      "do": "Show helpful message and action",
      "dont": "Blank empty screens",
      "severity": "Medium",
      "source": "ux",
      "score": 2.775615273023914
    },
    {
      "category": "Bundle Size",
      "issue": "Defer Third Party",
      "platform": "React/Next.js",
      "description": "Load analytics and logging after hydration since they don't block interaction",
      "do": "Load non-critical scripts after hydration",
      "dont": "Include analytics in main bundle",
      "severity": "Medium",
      "source": "react",
      "score": 2.7732067277363517
    },
    {
      "category": "Rerender",
      "issue": "Defer State Reads",
      "platform": "React/Next.js",
      "description": "Don't subscribe to state only used in callbacks",
      "do": "Read state on-demand in callbacks",
      "dont": "Subscribe to state used only in handlers",
      "severity": "Medium",
      "source": "react",
      "score": 2.140695105973442
    }
  ],
  "checklist": [
    {
      "category": "视觉质量",
      "checks": [
        "禁止用 emoji 作为结构图标（使用 SVG/矢量图标库，如 Phosphor、Heroicons、Lucide）",
        "图标统一来自同一套图标家族，保持一致的描边粗细与风格（线性/填充不混用）",
        "使用官方品牌素材，比例与留白符合品牌规范",
        "按压态视觉效果不改变布局边界、不引起抖动",
        "全程使用语义化主题令牌（design tokens），禁止逐屏硬编码颜色",
        "图标尺寸以设计令牌定义（icon-sm/icon-md/icon-lg），不做任意值",
        "图标与文字基线对齐，间距一致"
      ]
    },
    {
      "category": "交互",
      "checks": [
        "所有可点击元素提供明确的按压反馈（水波纹/透明度/抬升），80-150ms 内响应",
        "触摸目标最小 44×44pt（iOS）/ 48×48dp（Android），小图标用 hitSlop 扩展热区",
        "微交互时长控制在 150-300ms，使用平台原生缓动",
        "禁用态语义清晰（disabled 属性 + 视觉弱化），看起来不可点却无响应是错误",
        "屏幕阅读器焦点顺序与视觉顺序一致，交互标签描述清晰",
        "避免嵌套手势冲突（tap/drag/返回手势互斥）",
        "优先使用原生交互组件（Button/Pressable），并带正确的无障碍角色",
        "可点击元素必须有 cursor:pointer"
      ]
    },
    {
      "category": "深色 / 浅色模式",
      "checks": [
        "浅色模式正文对比度 ≥ 4.5:1，深色模式正文 ≥ 4.5:1、次要文字 ≥ 3:1",
        "卡片/表面与背景有明显层级（足够的透明度/投影）",
        "分隔线/边框在两种主题下都可见（不能只在浅色下可见）",
        "按压/聚焦/禁用态在深浅色下可区分度一致",
        "用语义色令牌按主题映射，禁止硬编码逐屏 hex",
        "模态遮罩足够强（通常 40-60% 黑色）保证前景可读",
        "交付前必须独立测试两种主题（不能从单一主题推断）"
      ]
    },
    {
      "category": "布局与间距",
      "checks": [
        "遵守顶部/底部安全区（刘海、状态栏、手势条），固定头部/标签栏/CTA 不遮挡内容",
        "滚动内容不被固定/粘性栏遮挡（列表底部加内容内边距）",
        "在 375px 小屏、大屏、平板（横竖屏）上验证",
        "水平内边距随断点/横屏自适应，不能所有尺寸同一窄边距",
        "保持 4/8dp 间距节奏（组件、区块、页面层级一致）",
        "长文在大屏上保持可读行长（避免平板全宽段落）",
        "定义清晰纵向节奏层级（16/24/32/48）"
      ]
    },
    {
      "category": "无障碍",
      "checks": [
        "有意义的图片/图标都有无障碍标签（accessibilityLabel / alt）",
        "表单字段有标签、提示与清晰的错误信息",
        "颜色不是唯一的信息指示方式",
        "支持减少动态（prefers-reduced-motion）与动态字体（Dynamic Type）而不破坏布局",
        "无障碍角色/状态（selected/disabled/expanded）正确播报",
        "键盘导航焦点可见（focus-visible）"
      ]
    },
    {
      "category": "性能（React / 移动端）",
      "checks": [
        "长列表虚拟化（virtualize-lists），避免渲染全部节点",
        "主线程预算：动画/布局不阻塞主线程（main-thread-budget）",
        "防抖/节流高频事件（debounce-throttle）",
        "避免瀑布请求（waterfall），Suspense + 动态导入拆分 bundle",
        "memo/useCallback 控制重渲染，避免无关 state 变化触发列表重渲染"
      ]
    },
    {
      "category": "表单与输入",
      "checks": [
        "内联校验（inline-validation），错误即时且清晰",
        "错误信息具体可操作（error-clarity），不只标红",
        "焦点管理（focus-management）：提交/关闭后焦点落位正确",
        "输入类型正确（input type / autocomplete），移动端弹出正确键盘"
      ]
    },
    {
      "category": "导航与信息架构",
      "checks": [
        "导航层级清晰（nav-hierarchy），底部导航不超过 4-5 项（bottom-nav-limit）",
        "返回行为符合平台习惯（back-behavior）",
        "深链接/状态恢复：切后台回来不丢状态",
        "空状态、加载态、错误态都有设计（empty/loading/error states）"
      ]
    }
  ]
}
