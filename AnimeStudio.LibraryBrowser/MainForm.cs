using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class MainForm : Form
    {
        private enum AnimationDiagnosticsMode
        {
            GateOnly,
            FastSummary,
            SummaryOnly,
        }

        private const int LargeIconCellWidth = 240;
        private const int LargeIconCellHeight = 204;
        private const int LvmFirst = 0x1000;
        private const int LvmSetIconSpacing = LvmFirst + 53;

        private readonly TableLayoutPanel _rootLayout = new();
        private readonly TableLayoutPanel _toolbarPanel = new();
        private readonly ToolStrip _commandStrip = new();
        private readonly ToolStrip _searchStrip = new();
        private readonly ToolStrip _filterStrip = new();
        private readonly ToolStripButton _openButton = new("选择素材库");
        private readonly ToolStripDropDownButton _recentButton = new("最近");
        private readonly ToolStripButton _refreshListButton = new("刷新列表");
        private readonly ToolStripButton _reloadButton = new("重新加载");
        private readonly ToolStripDropDownButton _refreshAnimationGateButton = new("刷新动画门禁");
        private readonly ToolStripMenuItem _refreshAnimationGateFastItem = new("快速门禁");
        private readonly ToolStripMenuItem _refreshAnimationFastSummaryItem = new("快速摘要");
        private readonly ToolStripMenuItem _refreshAnimationSummaryItem = new("烘焙摘要");
        private readonly ToolStripMenuItem _writeAvatarRecoveryPlanItem = new("Avatar恢复计划");
        private readonly ToolStripMenuItem _recoverImportedAvatarAssetsItem = new("恢复AvatarAsset");
        private readonly ToolStripMenuItem _probeImportedAvatarAssetsItem = new("验证AvatarAsset");
        private readonly ToolStripButton _rebuildAnimationIndexButton = new("重建动画索引");
        private readonly ToolStripButton _unitySettingsButton = new("Unity设置");
        private readonly ToolStripDropDownButton _unityWorkerButton = new("Unity Worker");
        private readonly ToolStripMenuItem _unityWorkerStatusItem = new("状态: 未选择素材库");
        private readonly ToolStripMenuItem _startUnityWorkerItem = new("启动");
        private readonly ToolStripMenuItem _restartUnityWorkerItem = new("重启");
        private readonly ToolStripMenuItem _stopUnityWorkerItem = new("停止");
        private readonly ToolStripTextBox _searchBox = new();
        private readonly ToolStripButton _clearSearchButton = new("清除");
        private readonly ToolStripLabel _typeLabel = new("类型");
        private readonly ToolStripLabel _qualityLabel = new("质量");
        private readonly ToolStripComboBox _kindBox = new();
        private readonly ToolStripComboBox _qualityBox = new();
        private readonly ToolStripComboBox _thumbnailStateBox = new();
        private readonly ToolStripComboBox _concurrencyBox = new();
        private readonly ToolStripButton _showFavoriteModelsButton = new("收藏模型");
        private readonly ToolStripButton _hideIgnoredButton = new("隐藏忽略");
        private readonly TabControl _mainTabs = new();
        private readonly TabPage _modelsPage = new("模型");
        private readonly TabPage _animationsPage = new("动画");
        private readonly TabPage _texturesPage = new("贴图");
        private readonly TabPage _vfxPage = new("VFX");
        private readonly SplitContainer _split = new();
        private readonly SplitContainer _detailSplit = new();
        private readonly ListView _modelList = new();
        private readonly ListView _animationList = new();
        private readonly ToolStrip _modelAnimationStrip = new();
        private readonly ToolStripTextBox _modelAnimationFilterBox = new();
        private readonly ToolStripComboBox _modelAnimationStateBox = new();
        private readonly ToolStripButton _clearModelAnimationFilterButton = new("清除");
        private readonly ToolStripButton _showFavoriteModelAnimationsButton = new("收藏");
        private readonly ToolStrip _libraryAnimationStrip = new();
        private readonly ToolStripTextBox _animationModelFilterBox = new();
        private readonly ToolStripComboBox _animationModelStateBox = new();
        private readonly ToolStripButton _clearAnimationModelSearchButton = new("清除");
        private readonly ToolStripButton _clearAnimationModelFilterButton = new("全部模型");
        private readonly ToolStripButton _showFavoriteLibraryAnimationsButton = new("收藏");
        private readonly ToolStripButton _bakeSelectedAnimationModelsButton = new("烘焙选中模型");
        private readonly ToolStripButton _bakeVisibleAnimationModelsButton = new("烘焙可见模型");
        private readonly ListView _libraryAnimationList = new();
        private readonly ListView _animationModelList = new();
        private readonly TextBox _animationDetailBox = new();
        private readonly SplitContainer _animationPageSplit = new();
        private readonly SplitContainer _animationModelSplit = new();
        private readonly ListView _textureList = new();
        private readonly ImageList _textureImages = new();
        private readonly TextBox _textureDetailBox = new();
        private readonly SplitContainer _textureSplit = new();
        private readonly ListView _vfxList = new();
        private readonly TextBox _vfxDetailBox = new();
        private readonly SplitContainer _vfxSplit = new();
        private readonly SplitContainer _vfxDetailSplit = new();
        private readonly VfxPreviewControl _vfxPreview = new();
        private readonly ImageList _images = new();
        private readonly TextBox _detailBox = new();
        private readonly StatusStrip _statusStrip = new();
        private readonly ToolStripStatusLabel _statusLabel = new();
        private readonly ContextMenuStrip _menu = new();
        private readonly ContextMenuStrip _animationMenu = new();
        private readonly ContextMenuStrip _libraryAnimationMenu = new();
        private readonly System.Windows.Forms.Timer _uiRefreshTimer = new();
        private readonly System.Windows.Forms.Timer _unityWorkerStatusTimer = new();
        private readonly RecentLibraryStore _recentStore = new();
        private readonly HashSet<string> _queuedTextureThumbnails = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _textureThumbnailSlots = new(2, 2);

        private string _root;
        private List<LibraryModelItem> _allModels = new();
        private List<LibraryModelItem> _visibleModels = new();
        private List<LibraryModelItem> _allTextures = new();
        private List<LibraryModelItem> _visibleTextures = new();
        private List<LibraryModelItem> _allVfx = new();
        private List<LibraryModelItem> _visibleVfx = new();
        private List<LibraryModelItem> _visibleAnimationModels = new();
        private ThumbnailCache _thumbnailCache;
        private LibraryCurationStore _curationStore;
        private LibraryAnimationIndex _animationIndex = LibraryAnimationIndex.Empty;
        private LibrarySourceIndexHealth _sourceIndexHealth = LibrarySourceIndexHealth.Empty;
        private LibraryBakeCacheSummary _bakeCacheSummary = LibraryBakeCacheSummary.Empty;
        private LibraryDeterministicAnimationSummary _deterministicAnimationSummary = LibraryDeterministicAnimationSummary.Empty;
        private AnimationPreviewCache _previewCache;
        private LibraryModelItem _detailModel;
        private List<LibraryAnimationCandidate> _detailAnimations = new();
        private List<LibraryAnimationCandidate> _visibleDetailAnimations = new();
        private List<LibraryAnimationUsage> _allLibraryAnimations = new();
        private List<LibraryAnimationUsage> _visibleLibraryAnimations = new();
        private LibraryAnimationUsage _selectedLibraryAnimation;
        private int _selectedAnimationIndexedModelCount;
        private int _selectedAnimationAvailableModelCount;
        private readonly List<ToolStripButton> _typeButtons = new();
        private string _selectedModelType = "全部";
        private CancellationTokenSource _thumbnailCts;
        private int _thumbnailTotal;
        private int _thumbnailCached;
        private int _thumbnailFailed;
        private int _thumbnailPending;
        private int _thumbnailActive;
        private bool _listRefreshRequested;
        private bool _statusRefreshRequested;
        private int _detailRequestId;

        public MainForm()
        {
            Text = "AnimeStudio Library Browser";
            Width = 1600;
            Height = 940;
            StartPosition = FormStartPosition.CenterScreen;

            BuildUi();
            WireEvents();
        }

        private void BuildUi()
        {
            _rootLayout.Dock = DockStyle.Fill;
            _rootLayout.ColumnCount = 1;
            _rootLayout.RowCount = 3;
            _rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _openButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _recentButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _refreshListButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _reloadButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _refreshAnimationGateButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _refreshAnimationGateButton.ToolTipText = "刷新动画确定性报告。快速摘要只读已有缓存和 ImportedAvatar；快速门禁检查显式关系；烘焙摘要统计 Unity bake / Avatar oracle 覆盖。";
            _refreshAnimationGateButton.DropDownItems.AddRange(new ToolStripItem[]
            {
                _refreshAnimationFastSummaryItem,
                _refreshAnimationGateFastItem,
                _refreshAnimationSummaryItem,
                new ToolStripSeparator(),
                _writeAvatarRecoveryPlanItem,
                _recoverImportedAvatarAssetsItem,
                _probeImportedAvatarAssetsItem,
            });
            _refreshAnimationFastSummaryItem.ToolTipText = "运行 FastSummary，只读取根目录 bake cache、ImportedAvatar asset 和最近批次报告，不扫描大型 SQLite 候选表。";
            _refreshAnimationGateFastItem.ToolTipText = "运行 GateOnly 动画关系门禁，检查默认候选是否全部来自 Unity 显式关系，并确认候选表 schema 已禁止写入非显式关系。";
            _refreshAnimationSummaryItem.ToolTipText = "运行 SummaryOnly 覆盖诊断；如果已配置 Unity Bake Project，会同步统计 ImportedAvatar oracle 覆盖。";
            _writeAvatarRecoveryPlanItem.ToolTipText = "生成 OnlyMissing Avatar asset 恢复计划，列出仍缺 ImportedAvatar oracle 的 Unity Avatar 对象。";
            _recoverImportedAvatarAssetsItem.ToolTipText = "从当前素材库的 Unity avatar source/pathId 自动恢复 ImportedAvatar/*.asset，并用 Unity 验证后接入 Browser。";
            _probeImportedAvatarAssetsItem.ToolTipText = "用 Unity AssetDatabase 批量验证 ImportedAvatar/*.asset 是否都是有效 Humanoid Avatar。";
            _rebuildAnimationIndexButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _rebuildAnimationIndexButton.ToolTipText = "只重建 library_index.db 的结构化索引和显式动画候选，不重新导出模型、贴图或动画。";
            _unitySettingsButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _unitySettingsButton.ToolTipText = "设置 LibraryBrowser 全局 Unity Editor 和 Unity Bake Project。素材库本地配置仍可覆盖全局配置。";
            _unityWorkerButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _unityWorkerButton.ToolTipText = "管理当前素材库的常驻 Unity Bake Worker。";
            _unityWorkerStatusItem.Enabled = false;
            _unityWorkerButton.DropDownItems.AddRange(new ToolStripItem[]
            {
                _unityWorkerStatusItem,
                new ToolStripSeparator(),
                _startUnityWorkerItem,
                _restartUnityWorkerItem,
                _stopUnityWorkerItem,
            });
            _searchBox.AutoSize = false;
            _searchBox.Width = 360;
            _searchBox.ToolTipText = "只按名称搜索，忽略大小写。支持 * ? 通配符、空格多词和 -排除。";
            _searchBox.TextBox.PlaceholderText = "搜索名称，例如 Env 或 *player*";
            _clearSearchButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _kindBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _kindBox.Width = 160;
            _qualityBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _qualityBox.Width = 160;
            _qualityBox.Items.AddRange(new object[] { "全部质量", "任务/道具", "任务需复查", "质量问题", "路径关系待补", "缺材质", "无外部贴图", "验证警告", "有动画", "有导入Avatar候选", "有原始Avatar候选", "待可信烘焙", "缺Avatar", "需Avatar元数据" });
            _qualityBox.SelectedIndex = 0;
            _thumbnailStateBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _thumbnailStateBox.Width = 120;
            _thumbnailStateBox.Items.AddRange(new object[] { "全部缩略图", "已有缩略图", "未生成", "生成失败" });
            _thumbnailStateBox.SelectedIndex = 0;
            _concurrencyBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _concurrencyBox.Width = 64;
            _concurrencyBox.Items.AddRange(new object[] { "2", "4", "6", "8", "12", "16", "24" });
            _concurrencyBox.SelectedItem = "16";
            _showFavoriteModelsButton.CheckOnClick = true;
            _showFavoriteModelsButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _hideIgnoredButton.CheckOnClick = true;
            _hideIgnoredButton.Checked = true;

            ConfigureToolStrip(_commandStrip);
            ConfigureToolStrip(_searchStrip);
            ConfigureToolStrip(_filterStrip);
            _toolbarPanel.Dock = DockStyle.Top;
            _toolbarPanel.AutoSize = true;
            _toolbarPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _toolbarPanel.ColumnCount = 2;
            _toolbarPanel.RowCount = 2;
            _toolbarPanel.Padding = new Padding(0, 2, 0, 2);
            _toolbarPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _toolbarPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _toolbarPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _toolbarPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _commandStrip.Items.AddRange(new ToolStripItem[]
            {
                _openButton,
                _recentButton,
                _refreshListButton,
                _reloadButton,
                _refreshAnimationGateButton,
                _rebuildAnimationIndexButton,
                _unitySettingsButton,
                _unityWorkerButton,
            });
            _searchStrip.Items.AddRange(new ToolStripItem[]
            {
                new ToolStripLabel("搜索"),
                _searchBox,
                _clearSearchButton,
                new ToolStripSeparator(),
                _typeLabel,
                new ToolStripSeparator(),
            });
            _filterStrip.Items.AddRange(new ToolStripItem[]
            {
                new ToolStripLabel("分类"),
                _kindBox,
                new ToolStripSeparator(),
                _qualityLabel,
                _qualityBox,
                new ToolStripSeparator(),
                new ToolStripLabel("缩略图"),
                _thumbnailStateBox,
                new ToolStripSeparator(),
                new ToolStripLabel("并发"),
                _concurrencyBox,
                new ToolStripSeparator(),
                _showFavoriteModelsButton,
                _hideIgnoredButton
            });
            _toolbarPanel.Controls.Add(_commandStrip, 0, 0);
            _toolbarPanel.SetColumnSpan(_commandStrip, 2);
            _toolbarPanel.Controls.Add(_searchStrip, 0, 1);
            _toolbarPanel.Controls.Add(_filterStrip, 1, 1);
            RebuildModelTypeFilter();
            RebuildRecentMenu();

            _images.ImageSize = new Size(144, 144);
            _images.ColorDepth = ColorDepth.Depth32Bit;
            _images.Images.Add("placeholder", CreatePlaceholderImage("等待"));
            _images.Images.Add("ignored", CreatePlaceholderImage("已忽略"));

            _modelList.Dock = DockStyle.Fill;
            _modelList.View = View.LargeIcon;
            _modelList.LargeImageList = _images;
            _modelList.HideSelection = false;
            _modelList.MultiSelect = true;
            _modelList.VirtualMode = true;
            _modelList.RetrieveVirtualItem += ModelList_RetrieveVirtualItem;
            _modelList.CacheVirtualItems += ModelList_CacheVirtualItems;
            _modelList.HandleCreated += (_, _) => SetLargeIconSpacing(_modelList);

            _detailBox.Dock = DockStyle.Fill;
            _detailBox.Multiline = true;
            _detailBox.ReadOnly = true;
            _detailBox.ScrollBars = ScrollBars.Vertical;
            _detailBox.Font = new Font(FontFamily.GenericMonospace, 9);
            _detailBox.BackColor = SystemColors.Control;
            _detailBox.ForeColor = SystemColors.GrayText;
            _detailBox.BorderStyle = BorderStyle.FixedSingle;

            _animationList.Dock = DockStyle.Fill;
            _animationList.View = View.Details;
            _animationList.FullRowSelect = true;
            _animationList.HideSelection = false;
            _animationList.Columns.Add("状态", 84);
            _animationList.Columns.Add("来源", 72);
            _animationList.Columns.Add("动画", 460);
            _animationList.Columns.Add("时长", 64);
            _animationList.Columns.Add("命中骨骼", 88);
            _animationList.Columns.Add("能力", 220);
            _animationList.DoubleClick += async (_, _) => await GenerateSelectedAnimationPreviewAsync(openAfterGenerate: true);
            _animationList.KeyDown += AnimationList_KeyDown;
            _animationList.MouseDown += (_, e) => SelectListViewItemOnRightClick(_animationList, e);

            ConfigureToolStrip(_modelAnimationStrip);
            _modelAnimationFilterBox.AutoSize = false;
            _modelAnimationFilterBox.Width = 280;
            _modelAnimationFilterBox.ToolTipText = "过滤当前模型的动画，支持动画名、路径、来源和当前预览/烘焙状态。";
            _modelAnimationFilterBox.TextBox.PlaceholderText = "过滤当前模型动画，例如 VampireMale_";
            _modelAnimationStateBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _modelAnimationStateBox.Width = 130;
            _modelAnimationStateBox.ToolTipText = "按当前模型下每个动画的预览/烘焙状态过滤，方便批量重试失败或待烘焙项。";
            _modelAnimationStateBox.Items.AddRange(new object[] { "全部状态", "可播放", "待可信烘焙", "导入Avatar", "原始Avatar", "需Avatar元数据", "失败/需复查" });
            _modelAnimationStateBox.SelectedIndex = 0;
            _clearModelAnimationFilterButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _showFavoriteModelAnimationsButton.CheckOnClick = true;
            _showFavoriteModelAnimationsButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _modelAnimationStrip.Items.AddRange(new ToolStripItem[]
            {
                new ToolStripLabel("过滤"),
                _modelAnimationFilterBox,
                _modelAnimationStateBox,
                _clearModelAnimationFilterButton,
                new ToolStripSeparator(),
                _showFavoriteModelAnimationsButton
            });

            ConfigureToolStrip(_libraryAnimationStrip);
            _animationModelFilterBox.AutoSize = false;
            _animationModelFilterBox.Width = 240;
            _animationModelFilterBox.ToolTipText = "过滤右侧关联模型，支持模型名、路径、分类和预览/烘焙状态。";
            _animationModelFilterBox.TextBox.PlaceholderText = "过滤模型，例如 Charlotte 或 可播放";
            _animationModelStateBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _animationModelStateBox.Width = 130;
            _animationModelStateBox.ToolTipText = "按当前动画对右侧模型的预览/烘焙状态过滤。";
            _animationModelStateBox.Items.AddRange(new object[] { "全部状态", "可播放", "待可信烘焙", "导入Avatar", "原始Avatar", "需Avatar元数据", "失败/需复查" });
            _animationModelStateBox.SelectedIndex = 0;
            _clearAnimationModelSearchButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _clearAnimationModelFilterButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _showFavoriteLibraryAnimationsButton.CheckOnClick = true;
            _showFavoriteLibraryAnimationsButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _bakeSelectedAnimationModelsButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _bakeSelectedAnimationModelsButton.ToolTipText = "对当前选中的动画，Unity 烘焙右侧选中的模型。只处理显式关系里的 Humanoid/Muscle 候选。";
            _bakeVisibleAnimationModelsButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _bakeVisibleAnimationModelsButton.ToolTipText = "对当前选中的动画，Unity 烘焙右侧当前可见的模型。适合先用搜索缩小范围。";
            _libraryAnimationStrip.Items.AddRange(new ToolStripItem[]
            {
                new ToolStripLabel("模型过滤"),
                _animationModelFilterBox,
                _animationModelStateBox,
                _clearAnimationModelSearchButton,
                new ToolStripSeparator(),
                _showFavoriteLibraryAnimationsButton,
                _clearAnimationModelFilterButton,
                new ToolStripSeparator(),
                _bakeSelectedAnimationModelsButton,
                _bakeVisibleAnimationModelsButton
            });

            _libraryAnimationList.Dock = DockStyle.Fill;
            _libraryAnimationList.View = View.Details;
            _libraryAnimationList.FullRowSelect = true;
            _libraryAnimationList.HideSelection = false;
            _libraryAnimationList.Columns.Add("动画", 360);
            _libraryAnimationList.Columns.Add("索引模型数", 88);
            _libraryAnimationList.Columns.Add("时长", 64);
            _libraryAnimationList.Columns.Add("能力", 140);
            _libraryAnimationList.Columns.Add("路径", 360);
            _libraryAnimationList.KeyDown += LibraryAnimationList_KeyDown;
            _libraryAnimationList.MouseDown += (_, e) => SelectListViewItemOnRightClick(_libraryAnimationList, e);

            _animationModelList.Dock = DockStyle.Fill;
            _animationModelList.View = View.Details;
            _animationModelList.FullRowSelect = true;
            _animationModelList.HideSelection = false;
            _animationModelList.Columns.Add("模型", 260);
            _animationModelList.Columns.Add("类型", 72);
            _animationModelList.Columns.Add("分类", 120);
            _animationModelList.Columns.Add("骨骼", 64);
            _animationModelList.Columns.Add("预览/烘焙", 110);
            _animationModelList.Columns.Add("路径", 520);
            _animationModelList.DoubleClick += async (_, _) => await GenerateSelectedLibraryAnimationPreviewAsync(openAfterGenerate: true);

            _animationDetailBox.Dock = DockStyle.Fill;
            _animationDetailBox.Multiline = true;
            _animationDetailBox.ReadOnly = true;
            _animationDetailBox.ScrollBars = ScrollBars.Vertical;
            _animationDetailBox.Font = new Font(FontFamily.GenericMonospace, 9);
            _animationDetailBox.BackColor = SystemColors.Control;
            _animationDetailBox.ForeColor = SystemColors.GrayText;
            _animationDetailBox.BorderStyle = BorderStyle.FixedSingle;

            _textureImages.ImageSize = new Size(144, 144);
            _textureImages.ColorDepth = ColorDepth.Depth32Bit;
            _textureImages.Images.Add("texture-placeholder", CreatePlaceholderImage("贴图"));
            _textureList.Dock = DockStyle.Fill;
            _textureList.View = View.LargeIcon;
            _textureList.LargeImageList = _textureImages;
            _textureList.HideSelection = false;
            _textureList.MultiSelect = true;
            _textureList.VirtualMode = true;
            _textureList.RetrieveVirtualItem += TextureList_RetrieveVirtualItem;
            _textureList.CacheVirtualItems += TextureList_CacheVirtualItems;
            _textureList.DoubleClick += (_, _) => OpenSelectedTexture();

            _textureDetailBox.Dock = DockStyle.Fill;
            _textureDetailBox.Multiline = true;
            _textureDetailBox.ReadOnly = true;
            _textureDetailBox.ScrollBars = ScrollBars.Vertical;
            _textureDetailBox.Font = new Font(FontFamily.GenericMonospace, 9);
            _textureDetailBox.BackColor = SystemColors.Control;
            _textureDetailBox.ForeColor = SystemColors.GrayText;
            _textureDetailBox.BorderStyle = BorderStyle.FixedSingle;

            _vfxList.Dock = DockStyle.Fill;
            _vfxList.View = View.LargeIcon;
            _vfxList.LargeImageList = _images;
            _vfxList.HideSelection = false;
            _vfxList.MultiSelect = true;
            _vfxList.VirtualMode = true;
            _vfxList.RetrieveVirtualItem += VfxList_RetrieveVirtualItem;
            _vfxList.CacheVirtualItems += VfxList_CacheVirtualItems;
            _vfxList.DoubleClick += (_, _) => OpenSelectedVfxFolder();
            _vfxList.HandleCreated += (_, _) => SetLargeIconSpacing(_vfxList);

            _vfxDetailBox.Dock = DockStyle.Fill;
            _vfxDetailBox.Multiline = true;
            _vfxDetailBox.ReadOnly = true;
            _vfxDetailBox.ScrollBars = ScrollBars.Vertical;
            _vfxDetailBox.Font = new Font(FontFamily.GenericMonospace, 9);
            _vfxDetailBox.BackColor = SystemColors.Control;
            _vfxDetailBox.ForeColor = SystemColors.GrayText;
            _vfxDetailBox.BorderStyle = BorderStyle.FixedSingle;

            _split.Dock = DockStyle.Fill;
            _split.SplitterDistance = 1100;
            _split.Panel1.Controls.Add(_modelList);
            _detailSplit.Dock = DockStyle.Fill;
            _detailSplit.Orientation = Orientation.Horizontal;
            _detailSplit.SplitterDistance = 520;
            _modelAnimationStrip.Dock = DockStyle.Top;
            _detailSplit.Panel1.Controls.Add(_animationList);
            _detailSplit.Panel1.Controls.Add(_modelAnimationStrip);
            _detailSplit.Panel2.Controls.Add(_detailBox);
            _split.Panel2.Controls.Add(_detailSplit);

            _libraryAnimationStrip.Dock = DockStyle.Top;
            _animationPageSplit.Dock = DockStyle.Fill;
            _animationPageSplit.SplitterDistance = 620;
            _animationPageSplit.Panel1.Controls.Add(_libraryAnimationList);
            _animationPageSplit.Panel1.Controls.Add(_libraryAnimationStrip);
            _animationModelSplit.Dock = DockStyle.Fill;
            _animationModelSplit.Orientation = Orientation.Horizontal;
            _animationModelSplit.SplitterDistance = 520;
            _animationModelSplit.Panel1.Controls.Add(_animationModelList);
            _animationModelSplit.Panel2.Controls.Add(_animationDetailBox);
            _animationPageSplit.Panel2.Controls.Add(_animationModelSplit);

            _textureSplit.Dock = DockStyle.Fill;
            _textureSplit.SplitterDistance = 1100;
            _textureSplit.Panel1.Controls.Add(_textureList);
            _textureSplit.Panel2.Controls.Add(_textureDetailBox);

            _vfxPreview.Dock = DockStyle.Fill;
            _vfxSplit.Dock = DockStyle.Fill;
            _vfxSplit.SplitterDistance = 1100;
            _vfxSplit.Panel1.Controls.Add(_vfxList);
            _vfxDetailSplit.Dock = DockStyle.Fill;
            _vfxDetailSplit.Orientation = Orientation.Horizontal;
            _vfxDetailSplit.SplitterDistance = 520;
            _vfxDetailSplit.Panel1.Controls.Add(_vfxPreview);
            _vfxDetailSplit.Panel2.Controls.Add(_vfxDetailBox);
            _vfxSplit.Panel2.Controls.Add(_vfxDetailSplit);

            _modelsPage.Controls.Add(_split);
            _animationsPage.Controls.Add(_animationPageSplit);
            _texturesPage.Controls.Add(_textureSplit);
            _vfxPage.Controls.Add(_vfxSplit);
            _mainTabs.Dock = DockStyle.Fill;
            _mainTabs.TabPages.Add(_modelsPage);
            _mainTabs.TabPages.Add(_animationsPage);
            _mainTabs.TabPages.Add(_texturesPage);
            _mainTabs.TabPages.Add(_vfxPage);

            _statusStrip.Items.Add(_statusLabel);
            _rootLayout.Controls.Add(_toolbarPanel, 0, 0);
            _rootLayout.Controls.Add(_mainTabs, 0, 1);
            _rootLayout.Controls.Add(_statusStrip, 0, 2);
            Controls.Add(_rootLayout);

            _kindBox.Items.Add("All");
            _kindBox.SelectedIndex = 0;

            _menu.Items.Add("用 f3d 打开", null, (_, _) => OpenSelectedWithF3d());
            _menu.Items.Add("打开所在目录", null, (_, _) => OpenSelectedFolder());
            _menu.Items.Add("导出 FBX 模型", null, async (_, _) => await ExportSelectedModelFbxAsync());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("复制匹配动画路径", null, (_, _) => CopySelectedAnimationPaths());
            _menu.Items.Add("打开首个动画目录", null, (_, _) => OpenFirstAnimationFolder());
            _menu.Items.Add("生成并打开动画预览/Unity烘焙", null, async (_, _) => await GenerateSelectedAnimationPreviewAsync(openAfterGenerate: true));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("加入忽略", null, (_, _) => SetSelectedIgnored(true));
            _menu.Items.Add("取消忽略", null, (_, _) => SetSelectedIgnored(false));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("加入模型收藏", null, (_, _) => SetSelectedModelsFavorite(true));
            _menu.Items.Add("取消模型收藏", null, (_, _) => SetSelectedModelsFavorite(false));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("复制路径", null, (_, _) => CopySelectedPath());
            _modelList.ContextMenuStrip = _menu;

            _animationMenu.Items.Add("复制动画名", null, (_, _) => CopySelectedAnimationNames());
            _animationMenu.Items.Add("复制动画路径", null, (_, _) => CopySelectedAnimationBestPaths());
            _animationMenu.Items.Add(new ToolStripSeparator());
            _animationMenu.Items.Add("加入动画收藏", null, (_, _) => SetSelectedModelAnimationsFavorite(true));
            _animationMenu.Items.Add("取消动画收藏", null, (_, _) => SetSelectedModelAnimationsFavorite(false));
            _animationMenu.Items.Add(new ToolStripSeparator());
            _animationMenu.Items.Add("打开动画目录", null, (_, _) => OpenSelectedAnimationFolder());
            _animationMenu.Items.Add("生成并打开动画预览/Unity烘焙", null, async (_, _) => await GenerateSelectedAnimationPreviewAsync(openAfterGenerate: true));
            _animationMenu.Items.Add("导出 FBX 动画(无Skin)", null, async (_, _) => await ExportSelectedAnimationFbxAsync());
            _animationMenu.Items.Add("批量烘焙选中动画", null, async (_, _) => await BakeSelectedModelAnimationsAsync());
            _animationMenu.Items.Add("批量烘焙当前可见动画", null, async (_, _) => await BakeVisibleModelAnimationsAsync());
            _animationList.ContextMenuStrip = _animationMenu;

            _libraryAnimationMenu.Items.Add("复制动画名", null, (_, _) => CopySelectedLibraryAnimationNames());
            _libraryAnimationMenu.Items.Add("复制动画路径", null, (_, _) => CopySelectedLibraryAnimationBestPaths());
            _libraryAnimationMenu.Items.Add(new ToolStripSeparator());
            _libraryAnimationMenu.Items.Add("加入动画收藏", null, (_, _) => SetSelectedLibraryAnimationsFavorite(true));
            _libraryAnimationMenu.Items.Add("取消动画收藏", null, (_, _) => SetSelectedLibraryAnimationsFavorite(false));
            _libraryAnimationMenu.Items.Add(new ToolStripSeparator());
            _libraryAnimationMenu.Items.Add("显示可用模型", null, (_, _) => SelectLibraryAnimationFilter());
            _libraryAnimationMenu.Items.Add("生成并打开预览/Unity烘焙", null, async (_, _) => await GenerateSelectedLibraryAnimationPreviewAsync(openAfterGenerate: true));
            _libraryAnimationMenu.Items.Add("清除模型列表", null, (_, _) => ClearSelectedLibraryAnimation());
            _libraryAnimationList.ContextMenuStrip = _libraryAnimationMenu;
        }

        private static void ConfigureToolStrip(ToolStrip strip)
        {
            strip.GripStyle = ToolStripGripStyle.Hidden;
            strip.Dock = DockStyle.Fill;
            strip.AutoSize = true;
            strip.CanOverflow = true;
            strip.Padding = new Padding(6, 2, 6, 2);
        }

        private static void SelectListViewItemOnRightClick(ListView list, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            var item = list.GetItemAt(e.X, e.Y);
            if (item == null)
            {
                return;
            }

            item.Selected = true;
            item.Focused = true;
        }

        private void WireEvents()
        {
            _openButton.Click += async (_, _) => await ChooseRootAsync();
            _refreshListButton.Click += (_, _) => RefreshListOnly();
            _reloadButton.Click += async (_, _) => await ReloadAsync();
            _refreshAnimationGateButton.Click += async (_, _) => await RefreshAnimationGateAsync(AnimationDiagnosticsMode.GateOnly);
            _refreshAnimationFastSummaryItem.Click += async (_, _) => await RefreshAnimationGateAsync(AnimationDiagnosticsMode.FastSummary);
            _refreshAnimationGateFastItem.Click += async (_, _) => await RefreshAnimationGateAsync(AnimationDiagnosticsMode.GateOnly);
            _refreshAnimationSummaryItem.Click += async (_, _) => await RefreshAnimationGateAsync(AnimationDiagnosticsMode.SummaryOnly);
            _writeAvatarRecoveryPlanItem.Click += async (_, _) => await WriteAvatarRecoveryPlanAsync();
            _recoverImportedAvatarAssetsItem.Click += async (_, _) => await RecoverImportedAvatarAssetsAsync();
            _probeImportedAvatarAssetsItem.Click += async (_, _) => await ProbeImportedAvatarAssetsAsync();
            _rebuildAnimationIndexButton.Click += async (_, _) => await RebuildAnimationIndexAsync();
            _unitySettingsButton.Click += (_, _) => ConfigureUnitySettings();
            _startUnityWorkerItem.Click += async (_, _) => await StartUnityWorkerAsync();
            _restartUnityWorkerItem.Click += async (_, _) => await RestartUnityWorkerAsync();
            _stopUnityWorkerItem.Click += async (_, _) => await StopUnityWorkerAsync();
            _searchBox.TextChanged += (_, _) => ApplyFilter();
            _searchBox.KeyDown += SearchBox_KeyDown;
            _clearSearchButton.Click += (_, _) => ClearSearch();
            _clearAnimationModelFilterButton.Click += (_, _) => ClearSelectedLibraryAnimation();
            _animationModelFilterBox.TextChanged += (_, _) => RebuildAnimationModelList();
            _animationModelStateBox.SelectedIndexChanged += (_, _) => RebuildAnimationModelList();
            _clearAnimationModelSearchButton.Click += (_, _) =>
            {
                _animationModelFilterBox.Clear();
                _animationModelStateBox.SelectedIndex = 0;
            };
            _libraryAnimationList.SelectedIndexChanged += (_, _) => SelectLibraryAnimationFilter();
            _bakeSelectedAnimationModelsButton.Click += async (_, _) => await BakeSelectedLibraryAnimationModelsAsync();
            _bakeVisibleAnimationModelsButton.Click += async (_, _) => await BakeVisibleLibraryAnimationModelsAsync();
            _modelAnimationFilterBox.TextChanged += (_, _) => RebuildAnimationList();
            _modelAnimationStateBox.SelectedIndexChanged += (_, _) => RebuildAnimationList();
            _clearModelAnimationFilterButton.Click += (_, _) =>
            {
                _modelAnimationFilterBox.Clear();
                _modelAnimationStateBox.SelectedIndex = 0;
            };
            _kindBox.SelectedIndexChanged += (_, _) => ApplyFilter();
            _qualityBox.SelectedIndexChanged += (_, _) => ApplyFilter();
            _thumbnailStateBox.SelectedIndexChanged += (_, _) => ApplyFilter();
            _concurrencyBox.SelectedIndexChanged += (_, _) => RestartThumbnailQueue();
            _showFavoriteModelsButton.CheckedChanged += (_, _) => ApplyFilter();
            _hideIgnoredButton.CheckedChanged += (_, _) => ApplyFilter();
            _showFavoriteModelAnimationsButton.CheckedChanged += (_, _) => RebuildAnimationList();
            _showFavoriteLibraryAnimationsButton.CheckedChanged += (_, _) => RebuildLibraryAnimationList();
            _modelList.SelectedIndexChanged += (_, _) => UpdateDetails();
            _modelList.DoubleClick += (_, _) => OpenSelectedWithF3d();
            _textureList.SelectedIndexChanged += (_, _) => UpdateTextureDetails();
            _vfxList.SelectedIndexChanged += (_, _) => UpdateVfxDetails();
            _mainTabs.SelectedIndexChanged += (_, _) => SwitchPrimaryTab();
            _uiRefreshTimer.Interval = 500;
            _uiRefreshTimer.Tick += (_, _) => FlushQueuedUiRefresh();
            _uiRefreshTimer.Start();
            _unityWorkerStatusTimer.Interval = 5000;
            _unityWorkerStatusTimer.Tick += (_, _) => RefreshUnityWorkerStatus();
            _unityWorkerStatusTimer.Start();
            RefreshUnityWorkerStatus();
        }

        private async Task ChooseRootAsync()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择 AnimeStudio 导出的素材库目录"
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            await OpenLibraryAsync(dialog.SelectedPath);
        }

        private void ConfigureUnitySettings()
        {
            var global = LibraryBrowserSettings.LoadGlobal();
            var currentEditor = LibraryBrowserSettings.NormalizeUnityEditorPath(global.UnityEditor)
                ?? LibraryBrowserSettings.FindDefaultUnityEditor()
                ?? string.Empty;
            var currentProject = global.UnityProject ?? string.Empty;

            using var form = new Form
            {
                Text = "Unity Bake 设置",
                Width = 720,
                Height = 260,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                FormBorderStyle = FormBorderStyle.FixedDialog,
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 3,
                RowCount = 4,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var editorBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Text = currentEditor,
            };
            var projectBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Text = currentProject,
            };
            var editorBrowseButton = new Button
            {
                Text = "浏览...",
                AutoSize = true,
            };
            var projectBrowseButton = new Button
            {
                Text = "浏览...",
                AutoSize = true,
            };
            var infoLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(660, 0),
                Text = "Unity Editor 是 Unity.exe；Unity Bake Project 是用于运行 AnimeStudio.UnityBake helper 的 Unity 工程目录。"
                    + "素材库根目录下的 .as_browser_cache\\unity_bake_settings.json 仍可覆盖这里的全局配置。",
            };
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
            };
            var okButton = new Button
            {
                Text = "保存",
                DialogResult = DialogResult.None,
                AutoSize = true,
            };
            var cancelButton = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                AutoSize = true,
            };

            editorBrowseButton.Click += (_, _) =>
            {
                using var dialog = new OpenFileDialog
                {
                    Title = "选择 Unity Editor (Unity.exe)",
                    Filter = "Unity Editor|Unity.exe|可执行文件|*.exe|所有文件|*.*",
                    CheckFileExists = true,
                    Multiselect = false,
                };

                var current = LibraryBrowserSettings.NormalizeUnityEditorPath(editorBox.Text);
                if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(current);
                    dialog.FileName = current;
                }

                if (dialog.ShowDialog(form) == DialogResult.OK)
                {
                    editorBox.Text = LibraryBrowserSettings.NormalizeUnityEditorPath(dialog.FileName) ?? dialog.FileName;
                }
            };

            projectBrowseButton.Click += (_, _) =>
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "选择 Unity Bake Project 工程目录",
                    UseDescriptionForTitle = true,
                };

                if (!string.IsNullOrWhiteSpace(projectBox.Text) && Directory.Exists(projectBox.Text))
                {
                    dialog.SelectedPath = projectBox.Text;
                }

                if (dialog.ShowDialog(form) == DialogResult.OK)
                {
                    projectBox.Text = dialog.SelectedPath;
                }
            };

            okButton.Click += (_, _) =>
            {
                var normalizedEditor = LibraryBrowserSettings.NormalizeUnityEditorPath(editorBox.Text);
                var project = projectBox.Text?.Trim().Trim('"');

                if (string.IsNullOrWhiteSpace(normalizedEditor) || !File.Exists(normalizedEditor))
                {
                    MessageBox.Show(form, "请选择有效的 Unity.exe。", "Unity 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (string.IsNullOrWhiteSpace(project) || !Directory.Exists(project))
                {
                    MessageBox.Show(form, "请选择有效的 Unity Bake Project 工程目录。这个目录不是 Unity.exe，而是一个 Unity Project 文件夹。", "Unity 设置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var assetsDir = Path.Combine(project, "Assets");
                if (!Directory.Exists(assetsDir))
                {
                    var result = MessageBox.Show(
                        form,
                        "这个目录缺少 Assets 文件夹，看起来不像 Unity Project。\n\n仍然保存吗？",
                        "Unity 设置",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                    if (result != DialogResult.Yes)
                    {
                        return;
                    }
                }

                LibraryBrowserSettings.SaveGlobal(new LibraryBrowserSettings
                {
                    UnityProject = project,
                    UnityEditor = normalizedEditor,
                });

                form.DialogResult = DialogResult.OK;
                form.Close();
            };

            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);

            layout.Controls.Add(new Label { Text = "Unity Editor", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            layout.Controls.Add(editorBox, 1, 0);
            layout.Controls.Add(editorBrowseButton, 2, 0);
            layout.Controls.Add(new Label { Text = "Bake Project", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            layout.Controls.Add(projectBox, 1, 1);
            layout.Controls.Add(projectBrowseButton, 2, 1);
            layout.Controls.Add(infoLabel, 0, 2);
            layout.SetColumnSpan(infoLabel, 3);
            layout.Controls.Add(buttons, 0, 3);
            layout.SetColumnSpan(buttons, 3);
            form.Controls.Add(layout);
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            if (form.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            MessageBox.Show(
                this,
                "已保存 LibraryBrowser 全局 Unity Bake 设置。\n\n"
                    + "Unity Editor:\n"
                    + LibraryBrowserSettings.NormalizeUnityEditorPath(editorBox.Text)
                    + "\n\nUnity Bake Project:\n"
                    + projectBox.Text.Trim().Trim('"')
                    + "\n\n配置文件：\n"
                    + LibraryBrowserSettings.GlobalSettingsPath
                    + "\n\n素材库根目录下的 .as_browser_cache\\unity_bake_settings.json 会覆盖这个全局配置。",
                "Unity 设置",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            RefreshUnityWorkerStatus();
        }

        private void RefreshUnityWorkerStatus()
        {
            var status = UnityBakeWorkerManager.GetStatus(_root);
            _unityWorkerButton.Text = status.IsRunning ? "Unity Worker: 运行中" : "Unity Worker";
            _unityWorkerStatusItem.Text = "状态: " + status.Label;
            _startUnityWorkerItem.Enabled = !string.IsNullOrWhiteSpace(_root) && !status.IsRunning;
            _restartUnityWorkerItem.Enabled = !string.IsNullOrWhiteSpace(_root);
            _stopUnityWorkerItem.Enabled = !string.IsNullOrWhiteSpace(_root) && status.IsRunning;
        }

        private async Task StartUnityWorkerAsync()
        {
            await RunUnityWorkerOperationAsync("启动 Unity Worker", (settings, progress, token) =>
                UnityBakeWorkerManager.StartAsync(_root, settings, progress, token));
        }

        private async Task RestartUnityWorkerAsync()
        {
            await RunUnityWorkerOperationAsync("重启 Unity Worker", (settings, progress, token) =>
                UnityBakeWorkerManager.RestartAsync(_root, settings, progress, token));
        }

        private async Task StopUnityWorkerAsync()
        {
            await RunUnityWorkerOperationAsync("停止 Unity Worker", (_, progress, token) =>
                UnityBakeWorkerManager.StopAsync(_root, progress, token));
        }

        private async Task RunUnityWorkerOperationAsync(
            string title,
            Func<LibraryBrowserSettings, IProgress<string>, CancellationToken, Task<UnityBakeWorkerOperationResult>> operation)
        {
            if (string.IsNullOrWhiteSpace(_root))
            {
                MessageBox.Show(this, "请先选择素材库。", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ToggleUnityWorkerControls(false);
            var settings = LibraryBrowserSettings.LoadEffective(_root);
            var progress = new Progress<string>(message =>
            {
                UpdateStatus(message);
                RefreshUnityWorkerStatus();
            });

            try
            {
                var result = await operation(settings, progress, CancellationToken.None);
                RefreshUnityWorkerStatus();
                UpdateStatus(result.Message);
                MessageBox.Show(
                    this,
                    result.Message,
                    title,
                    MessageBoxButtons.OK,
                    result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (OperationCanceledException)
            {
                UpdateStatus(title + " 已取消");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                ToggleUnityWorkerControls(true);
                RefreshUnityWorkerStatus();
            }
        }

        private void ToggleUnityWorkerControls(bool enabled)
        {
            _unityWorkerButton.Enabled = enabled;
            _unitySettingsButton.Enabled = enabled;
        }

        private async Task OpenLibraryAsync(string root)
        {
            _root = root;
            RefreshUnityWorkerStatus();
            await ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            if (string.IsNullOrWhiteSpace(_root))
            {
                return;
            }

            _thumbnailCts?.Cancel();
            _thumbnailCache?.Dispose();
            _thumbnailCts = new CancellationTokenSource();
            _statusLabel.Text = "正在读取素材库索引 ...";

            try
            {
                var root = _root;
                var models = await Task.Run(() => LibraryIndexReader.LoadModels(root));
                _allModels = models
                    .Where(x => !x.IsVfx && !x.IsTexture)
                    .OrderBy(x => x.ModelSourceLabel)
                    .ThenBy(x => x.ResourceKind)
                    .ThenBy(x => x.Name)
                    .ToList();
                _allTextures = models
                    .Where(x => x.IsTexture)
                    .OrderBy(x => x.ResourceKind)
                    .ThenBy(x => x.Name)
                    .ToList();
                _allVfx = models
                    .Where(x => x.IsVfx)
                    .OrderBy(x => x.VfxCategory)
                    .ThenBy(x => x.ResourceKind)
                    .ThenBy(x => x.Name)
                    .ToList();
                _thumbnailCache = new ThumbnailCache(root, GetThumbnailConcurrency());
                _curationStore = new LibraryCurationStore(root);
                _animationIndex = await Task.Run(() => LibraryAnimationIndex.Load(root));
                _sourceIndexHealth = await Task.Run(() => LibrarySourceIndexHealth.Load(root));
                _bakeCacheSummary = await Task.Run(() => LibraryBakeCacheSummary.Load(root));
                _deterministicAnimationSummary = await Task.Run(() => LibraryDeterministicAnimationSummary.Load(root));
                _allLibraryAnimations = _animationIndex.FindAllAnimations().ToList();
                _selectedLibraryAnimation = null;
                _previewCache = new AnimationPreviewCache(root);
                _recentStore.Add(root);
                RebuildRecentMenu();
                Text = $"AnimeStudio Library Browser - {Path.GetFileName(root)}";
                ResetThumbnailProgress();
                RebuildModelTypeFilter();
                RebuildKindFilter();
                RebuildLibraryAnimationList();
                ApplyFilter();
                RebuildAnimationModelList();
                UpdateToolbarForPrimaryTab();
                var thumbnailStatus = _thumbnailCache.HasF3d ? "缩略图后台生成中" : "没有找到 f3d.exe";
                UpdateStatus($"{thumbnailStatus}；{BuildAnimationIndexStatus()}");
                StartThumbnailQueue(_thumbnailCts.Token);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "加载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _statusLabel.Text = "加载失败";
            }
        }

        private string BuildAnimationIndexStatus()
        {
            var source = string.IsNullOrWhiteSpace(_animationIndex.LoadSource)
                ? "未加载"
                : _animationIndex.LoadSource;
            return $"动画索引: {source}，动画 {_allLibraryAnimations.Count}，预建候选 {_animationIndex.IndexedCandidateCount} / 模型 {_animationIndex.IndexedModelCount}，{_sourceIndexHealth.ShortLabel()}，{_deterministicAnimationSummary.ShortLabel()}，{_bakeCacheSummary.ShortLabel()}";
        }

        private void ReloadBakeCacheSummary()
        {
            _bakeCacheSummary = LibraryBakeCacheSummary.Load(_root);
        }

        private void ReloadDeterministicAnimationSummary()
        {
            _deterministicAnimationSummary = LibraryDeterministicAnimationSummary.Load(_root);
        }

        private async Task RefreshAnimationGateAsync(AnimationDiagnosticsMode mode)
        {
            if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root))
            {
                MessageBox.Show(this, "请先选择素材库。", "动画关系门禁", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var script = FindDeterministicAnimationGateScript();
            if (string.IsNullOrWhiteSpace(script))
            {
                MessageBox.Show(
                    this,
                    "没有找到 scripts\\Measure-DeterministicAnimationCoverage.ps1。请从 AnimeStudio 源码目录启动 Browser，或在命令行手动运行 GateOnly 门禁。",
                    "动画关系门禁",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var needsUnityProject = mode is AnimationDiagnosticsMode.SummaryOnly or AnimationDiagnosticsMode.FastSummary;
            var settings = needsUnityProject
                ? LibraryBrowserSettings.LoadEffective(_root)
                : null;
            var unityProject = needsUnityProject && Directory.Exists(settings?.UnityProject)
                ? settings.UnityProject
                : "";
            if (mode == AnimationDiagnosticsMode.SummaryOnly && string.IsNullOrWhiteSpace(unityProject))
            {
                var confirm = MessageBox.Show(
                    this,
                    "当前没有配置有效的 Unity Bake Project。可以继续刷新 SummaryOnly 报告，但 ImportedAvatar oracle 覆盖会保守显示为不可测或 0。\n\n是否继续？",
                    "刷新烘焙摘要",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes)
                {
                    return;
                }
            }
            else if (mode == AnimationDiagnosticsMode.SummaryOnly)
            {
                var confirm = MessageBox.Show(
                    this,
                    "这会运行 SummaryOnly 动画覆盖诊断，统计 Unity bake / Avatar oracle 覆盖。大库可能需要几分钟，但不会烘焙动画，也不会新增或猜测模型-动画关系。\n\n是否继续？",
                    "刷新烘焙摘要",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                if (confirm != DialogResult.Yes)
                {
                    return;
                }
            }

            var outputPrefix = mode switch
            {
                AnimationDiagnosticsMode.FastSummary => "AnimationRelationDiagnostics_BrowserFast_",
                AnimationDiagnosticsMode.SummaryOnly => "AnimationRelationDiagnostics_BrowserSummary_",
                _ => "AnimationRelationDiagnostics_BrowserGate_",
            };
            var outputDir = Path.Combine(_root, outputPrefix + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            _refreshAnimationGateButton.Enabled = false;
            UpdateStatus(AnimationDiagnosticsRunningStatus(mode));

            ProcessRunResult result;
            try
            {
                result = await RunDeterministicAnimationGateAsync(script, _root, outputDir, mode, unityProject);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, AnimationDiagnosticsFailedTitle(mode), MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatus(AnimationDiagnosticsFailedTitle(mode));
                return;
            }
            finally
            {
                _refreshAnimationGateButton.Enabled = true;
            }

            ReloadDeterministicAnimationSummary();
            ReloadBakeCacheSummary();
            RefreshCurrentModelDetailText();
            var successStatus = AnimationDiagnosticsSuccessStatus(mode);
            var warningStatus = AnimationDiagnosticsWarningStatus(mode);
            UpdateStatus(result.ExitCode == 0 ? successStatus : warningStatus);

            var message = $"输出目录: {outputDir}{Environment.NewLine}{Environment.NewLine}{TrimProcessOutput(result.CombinedOutput)}";
            MessageBox.Show(
                this,
                message,
                result.ExitCode == 0 ? successStatus : $"{AnimationDiagnosticsTitle(mode)}退出码 {result.ExitCode}",
                MessageBoxButtons.OK,
                result.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private static string AnimationDiagnosticsTitle(AnimationDiagnosticsMode mode) => mode switch
        {
            AnimationDiagnosticsMode.FastSummary => "动画快速摘要",
            AnimationDiagnosticsMode.SummaryOnly => "动画烘焙摘要",
            _ => "动画关系门禁",
        };

        private static string AnimationDiagnosticsRunningStatus(AnimationDiagnosticsMode mode) => mode switch
        {
            AnimationDiagnosticsMode.FastSummary => "正在刷新动画快速摘要",
            AnimationDiagnosticsMode.SummaryOnly => "正在刷新动画烘焙摘要",
            _ => "正在刷新动画关系门禁",
        };

        private static string AnimationDiagnosticsSuccessStatus(AnimationDiagnosticsMode mode) => mode switch
        {
            AnimationDiagnosticsMode.FastSummary => "动画快速摘要已刷新",
            AnimationDiagnosticsMode.SummaryOnly => "动画烘焙摘要已刷新",
            _ => "动画关系门禁通过",
        };

        private static string AnimationDiagnosticsWarningStatus(AnimationDiagnosticsMode mode) => mode switch
        {
            AnimationDiagnosticsMode.FastSummary => "动画快速摘要有警告",
            AnimationDiagnosticsMode.SummaryOnly => "动画烘焙摘要有警告",
            _ => "动画关系门禁有警告",
        };

        private static string AnimationDiagnosticsFailedTitle(AnimationDiagnosticsMode mode) => mode switch
        {
            AnimationDiagnosticsMode.FastSummary => "动画快速摘要失败",
            AnimationDiagnosticsMode.SummaryOnly => "动画烘焙摘要失败",
            _ => "动画关系门禁失败",
        };

        private async Task WriteAvatarRecoveryPlanAsync()
        {
            if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root))
            {
                MessageBox.Show(this, "请先选择素材库。", "Avatar恢复计划", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var script = FindRepoScript(@"scripts\Write-UnityAvatarAssetRecoveryPlan.ps1");
            if (string.IsNullOrWhiteSpace(script))
            {
                MessageBox.Show(
                    this,
                    "没有找到 scripts\\Write-UnityAvatarAssetRecoveryPlan.ps1。请从 AnimeStudio 源码目录启动 Browser，或在命令行手动运行恢复计划脚本。",
                    "Avatar恢复计划",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var settings = LibraryBrowserSettings.LoadEffective(_root);
            var unityProject = Directory.Exists(settings?.UnityProject) ? settings.UnityProject : "";
            if (string.IsNullOrWhiteSpace(unityProject))
            {
                var confirm = MessageBox.Show(
                    this,
                    "当前没有配置有效的 Unity Bake Project。可以继续生成恢复计划，但无法过滤已经导入的 ImportedAvatar asset。\n\n是否继续？",
                    "Avatar恢复计划",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes)
                {
                    return;
                }
            }

            var outputDir = Path.Combine(_root, "UnityAvatarAssetRecoveryPlan_BrowserMissing_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            _refreshAnimationGateButton.Enabled = false;
            UpdateStatus("正在生成Avatar恢复计划");

            ProcessRunResult result;
            try
            {
                result = await RunAvatarRecoveryPlanAsync(script, _root, outputDir, unityProject, settings?.UnityEditor ?? "");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Avatar恢复计划失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatus("Avatar恢复计划失败");
                return;
            }
            finally
            {
                _refreshAnimationGateButton.Enabled = true;
            }

            var title = result.ExitCode == 0 ? "Avatar恢复计划已生成" : $"Avatar恢复计划退出码 {result.ExitCode}";
            UpdateStatus(title);
            var message = $"输出目录: {outputDir}{Environment.NewLine}{Environment.NewLine}{TrimProcessOutput(result.CombinedOutput)}";
            MessageBox.Show(
                this,
                message,
                title,
                MessageBoxButtons.OK,
                result.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private async Task ProbeImportedAvatarAssetsAsync()
        {
            if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root))
            {
                MessageBox.Show(this, "请先选择素材库。", "验证AvatarAsset", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var script = FindRepoScript(@"scripts\Test-UnityImportedAvatarAssets.ps1");
            if (string.IsNullOrWhiteSpace(script))
            {
                MessageBox.Show(
                    this,
                    "没有找到 scripts\\Test-UnityImportedAvatarAssets.ps1。请从 AnimeStudio 源码目录启动 Browser，或在命令行手动运行验证脚本。",
                    "验证AvatarAsset",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var settings = LibraryBrowserSettings.LoadEffective(_root);
            var settingsError = settings?.ValidateUnityBake();
            if (!string.IsNullOrWhiteSpace(settingsError))
            {
                MessageBox.Show(this, settingsError, "验证AvatarAsset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var outputDir = Path.Combine(_root, "ImportedAvatarProbe_Browser_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            _refreshAnimationGateButton.Enabled = false;
            UpdateStatus("正在验证AvatarAsset");

            ProcessRunResult result;
            ProcessRunResult? fastSummaryResult = null;
            var fastSummaryOutputDir = "";
            try
            {
                result = await RunImportedAvatarProbeAsync(script, settings!.UnityProject, settings.UnityEditor, outputDir);
                if (result.ExitCode == 0)
                {
                    var fastSummaryScript = FindDeterministicAnimationGateScript();
                    if (!string.IsNullOrWhiteSpace(fastSummaryScript))
                    {
                        fastSummaryOutputDir = Path.Combine(_root, "AnimationRelationDiagnostics_BrowserFast_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                        UpdateStatus("AvatarAsset验证通过，正在刷新动画快速摘要");
                        fastSummaryResult = await RunDeterministicAnimationGateAsync(
                            fastSummaryScript,
                            _root,
                            fastSummaryOutputDir,
                            AnimationDiagnosticsMode.FastSummary,
                            settings.UnityProject);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "验证AvatarAsset失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatus("验证AvatarAsset失败");
                return;
            }
            finally
            {
                _refreshAnimationGateButton.Enabled = true;
            }

            if (fastSummaryResult != null)
            {
                ReloadDeterministicAnimationSummary();
                ReloadBakeCacheSummary();
                RefreshCurrentModelDetailText();
            }

            var title = result.ExitCode == 0
                ? fastSummaryResult?.ExitCode == 0
                    ? "AvatarAsset验证通过，快速摘要已刷新"
                    : "AvatarAsset验证通过"
                : $"AvatarAsset验证退出码 {result.ExitCode}";
            UpdateStatus(title);
            var message = $"验证输出目录: {outputDir}{Environment.NewLine}{Environment.NewLine}{TrimProcessOutput(result.CombinedOutput)}";
            if (fastSummaryResult != null)
            {
                message += Environment.NewLine
                    + Environment.NewLine
                    + $"快速摘要输出目录: {fastSummaryOutputDir}"
                    + Environment.NewLine
                    + Environment.NewLine
                    + TrimProcessOutput(fastSummaryResult.Value.CombinedOutput);
            }
            MessageBox.Show(
                this,
                message,
                title,
                MessageBoxButtons.OK,
                result.ExitCode == 0 && (fastSummaryResult == null || fastSummaryResult.Value.ExitCode == 0)
                    ? MessageBoxIcon.Information
                    : MessageBoxIcon.Warning);
        }

        private async Task RecoverImportedAvatarAssetsAsync()
        {
            if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root))
            {
                MessageBox.Show(this, "请先选择素材库。", "恢复AvatarAsset", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var settings = LibraryBrowserSettings.LoadEffective(_root);
            var settingsError = settings?.ValidateUnityBake();
            if (!string.IsNullOrWhiteSpace(settingsError))
            {
                MessageBox.Show(this, settingsError, "恢复AvatarAsset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var gameName = ReadSourceIndexGameName(_root);
            if (string.IsNullOrWhiteSpace(gameName))
            {
                MessageBox.Show(
                    this,
                    "没有从 unity_source_index.db 读到 game metadata，无法确定应该用哪个 Unity/game 加载规则恢复 Avatar。请先重建源索引，或用 CLI 显式传入 --game。",
                    "恢复AvatarAsset",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var launcher = FindCliLauncher();
            if (string.IsNullOrWhiteSpace(launcher.FileName))
            {
                MessageBox.Show(this, "没有找到 AnimeStudio.CLI，无法恢复 AvatarAsset。", "恢复AvatarAsset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                this,
                "这会从 library_index.db 记录的 Unity avatar source/pathId 恢复 ImportedAvatar/*.asset，随后用 Unity 验证并只接入有效 Humanoid Avatar。无效 Avatar 会被移到 InvalidImportedAvatar，不会用于 bake。\n\n是否继续？",
                "恢复AvatarAsset",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            _refreshAnimationGateButton.Enabled = false;
            UpdateStatus("正在恢复AvatarAsset");

            ProcessRunResult result;
            try
            {
                result = await RunRecoverImportedAvatarAssetsAsync(
                    launcher,
                    _root,
                    gameName,
                    settings!.UnityProject,
                    settings.UnityEditor);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "恢复AvatarAsset失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatus("恢复AvatarAsset失败");
                return;
            }
            finally
            {
                _refreshAnimationGateButton.Enabled = true;
            }

            await OpenLibraryAsync(_root);
            await RefreshAnimationGateAsync(AnimationDiagnosticsMode.FastSummary);
            var title = result.ExitCode == 0 ? "AvatarAsset恢复完成" : $"AvatarAsset恢复退出码 {result.ExitCode}";
            UpdateStatus(title);
            MessageBox.Show(
                this,
                TrimProcessOutput(result.CombinedOutput),
                title,
                MessageBoxButtons.OK,
                result.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private async Task RebuildAnimationIndexAsync()
        {
            if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root))
            {
                MessageBox.Show(this, "请先选择素材库。", "重建动画索引", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var sourceIndex = ResolveSourceIndexForAnimationRebuild(_root);
            if (!File.Exists(sourceIndex))
            {
                MessageBox.Show(
                    this,
                    "没有找到可用于重建的 unity_source_index.db。请先用完整 Unity 源目录重建源索引；如果你已经有旁路 fresh 源索引，请先用 CLI 的 --build_sqlite_index 加 --source_index 显式重建一次，让 sqlite_index_summary.json 记录实际源索引。",
                    "重建动画索引",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var launcher = FindCliLauncher();
            if (string.IsNullOrWhiteSpace(launcher.FileName))
            {
                MessageBox.Show(this, "没有找到 AnimeStudio.CLI，无法重建 library_index.db。", "重建动画索引", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                this,
                "这会覆盖当前素材库的 library_index.db，并刷新显式动画候选和候选表 schema。不会重新导出模型、贴图或动画。"
                + "\n\n使用源索引: " + sourceIndex
                + "\n\n大型库可能需要一段时间；运行期间请不要再打开同一个 library_index.db。是否继续？",
                "重建动画索引",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            _rebuildAnimationIndexButton.Enabled = false;
            _refreshAnimationGateButton.Enabled = false;
            UpdateStatus("正在重建 SQLite 动画索引");

            ProcessRunResult result;
            try
            {
                result = await RunBuildSqliteIndexAsync(launcher, _root, sourceIndex);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "重建动画索引失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatus("重建动画索引失败");
                return;
            }
            finally
            {
                _rebuildAnimationIndexButton.Enabled = true;
                _refreshAnimationGateButton.Enabled = true;
            }

            if (result.ExitCode != 0)
            {
                UpdateStatus($"重建动画索引失败: exit {result.ExitCode}");
                MessageBox.Show(
                    this,
                    TrimProcessOutput(result.CombinedOutput),
                    $"重建动画索引失败: exit {result.ExitCode}",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            await OpenLibraryAsync(_root);
            await RefreshAnimationGateAsync(AnimationDiagnosticsMode.GateOnly);
            UpdateStatus("SQLite 动画索引已重建");
            MessageBox.Show(
                this,
                "library_index.db 已重建，并已刷新动画关系门禁。\n\n" + TrimProcessOutput(result.CombinedOutput),
                "重建动画索引完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private static string ResolveSourceIndexForAnimationRebuild(string libraryRoot)
        {
            var fromSummary = LibrarySourceIndexHealth.Load(libraryRoot).PreferredSourceIndexPath;
            if (!string.IsNullOrWhiteSpace(fromSummary) && File.Exists(fromSummary))
            {
                return fromSummary;
            }

            return Path.Combine(libraryRoot, "unity_source_index.db");
        }

        private static string ReadSourceIndexGameName(string libraryRoot)
        {
            var sourceIndex = ResolveSourceIndexForAnimationRebuild(libraryRoot);
            if (string.IsNullOrWhiteSpace(sourceIndex) || !File.Exists(sourceIndex))
            {
                return "";
            }

            try
            {
                using var connection = new SqliteConnection($"Data Source={sourceIndex};Mode=ReadOnly");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM metadata WHERE key='game' LIMIT 1;";
                return command.ExecuteScalar() as string ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static async Task<ProcessRunResult> RunBuildSqliteIndexAsync(CliRunLauncher launcher, string libraryRoot, string sourceIndex)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = launcher.FileName,
                WorkingDirectory = launcher.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in launcher.BuildSqliteIndexArguments(libraryRoot, sourceIndex))
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 AnimeStudio.CLI。");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            var combined = string.IsNullOrWhiteSpace(error)
                ? output
                : output + Environment.NewLine + error;
            return new ProcessRunResult(process.ExitCode, combined);
        }

        private static async Task<ProcessRunResult> RunRecoverImportedAvatarAssetsAsync(
            CliRunLauncher launcher,
            string libraryRoot,
            string gameName,
            string unityProject,
            string unityEditor)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = launcher.FileName,
                WorkingDirectory = launcher.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in launcher.RecoverImportedAvatarArguments(libraryRoot, gameName, unityProject, unityEditor))
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 AnimeStudio.CLI。");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            var combined = string.IsNullOrWhiteSpace(error)
                ? output
                : output + Environment.NewLine + error;
            return new ProcessRunResult(process.ExitCode, combined);
        }

        private static CliRunLauncher FindCliLauncher()
        {
            foreach (var root in EnumerateCandidateRepoRoots())
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var exe = Path.Combine(root, "AnimeStudio.CLI", "bin", "Debug", "net9.0-windows", "AnimeStudio.CLI.exe");
                if (File.Exists(exe))
                {
                    return new CliRunLauncher(exe, root, (libraryRoot, sourceIndex) =>
                        BuildSqliteIndexArguments(libraryRoot, sourceIndex),
                        (libraryRoot, gameName, unityProject, unityEditor) =>
                            RecoverImportedAvatarArguments(libraryRoot, gameName, unityProject, unityEditor),
                        (inputGltf, outputFbx, skeletonOnly) =>
                            BuildFbxFromGltfArguments(inputGltf, outputFbx, skeletonOnly));
                }

                var project = Path.Combine(root, "AnimeStudio.CLI", "AnimeStudio.CLI.csproj");
                if (File.Exists(project))
                {
                    return new CliRunLauncher("dotnet", root, (libraryRoot, sourceIndex) =>
                        BuildSqliteIndexArguments(libraryRoot, sourceIndex, project),
                        (libraryRoot, gameName, unityProject, unityEditor) =>
                            RecoverImportedAvatarArguments(libraryRoot, gameName, unityProject, unityEditor, project),
                        (inputGltf, outputFbx, skeletonOnly) =>
                            BuildFbxFromGltfArguments(inputGltf, outputFbx, skeletonOnly, project));
                }
            }

            var siblingExe = Path.Combine(AppContext.BaseDirectory, "AnimeStudio.CLI.exe");
            if (File.Exists(siblingExe))
            {
                return new CliRunLauncher(siblingExe, AppContext.BaseDirectory, (libraryRoot, sourceIndex) =>
                    BuildSqliteIndexArguments(libraryRoot, sourceIndex),
                    (libraryRoot, gameName, unityProject, unityEditor) =>
                        RecoverImportedAvatarArguments(libraryRoot, gameName, unityProject, unityEditor),
                    (inputGltf, outputFbx, skeletonOnly) =>
                        BuildFbxFromGltfArguments(inputGltf, outputFbx, skeletonOnly));
            }

            return default;
        }

        private static async Task<ProcessRunResult> RunCliAsync(CliRunLauncher launcher, string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = launcher.FileName,
                WorkingDirectory = launcher.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 AnimeStudio.CLI。");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            var combined = string.IsNullOrWhiteSpace(error)
                ? output
                : output + Environment.NewLine + error;
            return new ProcessRunResult(process.ExitCode, combined);
        }

        private static string[] BuildFbxFromGltfArguments(string inputGltf, string outputFbx, bool skeletonOnly, string project = null)
        {
            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(project))
            {
                args.AddRange(new[]
                {
                    "run",
                    "--project", project,
                    "-f", "net9.0-windows",
                    "--",
                });
            }

            args.AddRange(new[]
            {
                "--export_fbx_from_gltf", inputGltf,
                "--baked_fbx_output", outputFbx,
            });

            if (skeletonOnly)
            {
                args.Add("--fbx_skeleton_only");
            }

            return args.ToArray();
        }

        private static string[] BuildSqliteIndexArguments(string libraryRoot, string sourceIndex, string project = null)
        {
            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(project))
            {
                args.AddRange(new[]
                {
                    "run",
                    "--project", project,
                    "-f", "net9.0-windows",
                    "--",
                });
            }

            args.AddRange(new[]
            {
                "--build_sqlite_index", libraryRoot,
                "--require_fresh_source_animation_relations",
                "--skip_sqlite_file_index",
                "--skip_sqlite_sidecar_scan",
                "--skip_sqlite_json_documents",
            });

            if (!string.IsNullOrWhiteSpace(sourceIndex)
                && !string.Equals(Path.GetFullPath(sourceIndex), Path.GetFullPath(Path.Combine(libraryRoot, "unity_source_index.db")), StringComparison.OrdinalIgnoreCase))
            {
                args.AddRange(new[] { "--source_index", sourceIndex });
            }

            return args.ToArray();
        }

        private static string[] RecoverImportedAvatarArguments(
            string libraryRoot,
            string gameName,
            string unityProject,
            string unityEditor,
            string project = null)
        {
            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(project))
            {
                args.AddRange(new[]
                {
                    "run",
                    "--project", project,
                    "-f", "net9.0-windows",
                    "--",
                });
            }

            args.AddRange(new[]
            {
                "--recover_imported_avatar_assets", libraryRoot,
                "--game", gameName,
                "--unity_project", unityProject,
                "--run_unity_bake",
            });

            if (!string.IsNullOrWhiteSpace(unityEditor))
            {
                args.AddRange(new[] { "--unity_editor", unityEditor });
            }

            return args.ToArray();
        }

        private static async Task<ProcessRunResult> RunDeterministicAnimationGateAsync(
            string script,
            string libraryRoot,
            string outputDir,
            AnimationDiagnosticsMode mode,
            string unityProject)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add("-LibraryPath");
            startInfo.ArgumentList.Add(libraryRoot);
            startInfo.ArgumentList.Add("-OutputDir");
            startInfo.ArgumentList.Add(outputDir);
            startInfo.ArgumentList.Add(mode switch
            {
                AnimationDiagnosticsMode.FastSummary => "-FastSummary",
                AnimationDiagnosticsMode.SummaryOnly => "-SummaryOnly",
                _ => "-GateOnly",
            });
            if (mode is AnimationDiagnosticsMode.FastSummary or AnimationDiagnosticsMode.SummaryOnly
                && !string.IsNullOrWhiteSpace(unityProject))
            {
                startInfo.ArgumentList.Add("-UnityProject");
                startInfo.ArgumentList.Add(unityProject);
            }
            if (mode != AnimationDiagnosticsMode.FastSummary)
            {
                startInfo.ArgumentList.Add("-FailOnWarning");
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 PowerShell。");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            var combined = string.IsNullOrWhiteSpace(error)
                ? output
                : output + Environment.NewLine + error;
            return new ProcessRunResult(process.ExitCode, combined);
        }

        private static async Task<ProcessRunResult> RunAvatarRecoveryPlanAsync(
            string script,
            string libraryRoot,
            string outputDir,
            string unityProject,
            string unityEditor)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add("-LibraryPath");
            startInfo.ArgumentList.Add(libraryRoot);
            startInfo.ArgumentList.Add("-OutputDir");
            startInfo.ArgumentList.Add(outputDir);
            startInfo.ArgumentList.Add("-Limit");
            startInfo.ArgumentList.Add("200");
            if (!string.IsNullOrWhiteSpace(unityProject))
            {
                startInfo.ArgumentList.Add("-UnityProject");
                startInfo.ArgumentList.Add(unityProject);
            }
            if (!string.IsNullOrWhiteSpace(unityEditor))
            {
                startInfo.ArgumentList.Add("-UnityEditor");
                startInfo.ArgumentList.Add(unityEditor);
            }
            startInfo.ArgumentList.Add("-OnlyMissing");

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 PowerShell。");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            var combined = string.IsNullOrWhiteSpace(error)
                ? output
                : output + Environment.NewLine + error;
            return new ProcessRunResult(process.ExitCode, combined);
        }

        private static async Task<ProcessRunResult> RunImportedAvatarProbeAsync(
            string script,
            string unityProject,
            string unityEditor,
            string outputDir)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add("-UnityProject");
            startInfo.ArgumentList.Add(unityProject);
            startInfo.ArgumentList.Add("-UnityEditor");
            startInfo.ArgumentList.Add(unityEditor);
            startInfo.ArgumentList.Add("-OutputDir");
            startInfo.ArgumentList.Add(outputDir);

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 PowerShell。");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = await outputTask;
            var error = await errorTask;
            var combined = string.IsNullOrWhiteSpace(error)
                ? output
                : output + Environment.NewLine + error;
            return new ProcessRunResult(process.ExitCode, combined);
        }

        private static string FindDeterministicAnimationGateScript()
        {
            return FindRepoScript(@"scripts\Measure-DeterministicAnimationCoverage.ps1");
        }

        private static string FindRepoScript(string relativeScript)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in EnumerateCandidateRepoRoots())
            {
                if (string.IsNullOrWhiteSpace(root) || !seen.Add(root))
                {
                    continue;
                }

                var candidate = Path.Combine(root, relativeScript);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateCandidateRepoRoots()
        {
            foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                var current = string.IsNullOrWhiteSpace(start) ? null : new DirectoryInfo(start);
                while (current != null)
                {
                    yield return current.FullName;
                    current = current.Parent;
                }
            }
        }

        private static string TrimProcessOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return "命令没有输出。";
            }

            const int maxLength = 4000;
            if (output.Length <= maxLength)
            {
                return output.Trim();
            }

            return "..." + output.Substring(output.Length - maxLength).TrimStart();
        }

        private static string SanitizeFileName(string value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "AnimeStudioExport" : value.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                text = text.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(text) ? "AnimeStudioExport" : text;
        }

        private readonly record struct ProcessRunResult(int ExitCode, string CombinedOutput);

        private readonly record struct CliRunLauncher(
            string FileName,
            string WorkingDirectory,
            Func<string, string, string[]> BuildSqliteIndexArguments,
            Func<string, string, string, string, string[]> RecoverImportedAvatarArguments,
            Func<string, string, bool, string[]> BuildFbxFromGltfArguments);

        private void RebuildRecentMenu()
        {
            _recentButton.DropDownItems.Clear();
            var recentPaths = _recentStore.Load().ToList();
            if (recentPaths.Count == 0)
            {
                _recentButton.DropDownItems.Add(new ToolStripMenuItem("暂无最近素材库") { Enabled = false });
                return;
            }

            foreach (var path in recentPaths)
            {
                var label = BuildRecentLabel(path);
                var item = new ToolStripMenuItem(label)
                {
                    ToolTipText = path
                };
                item.Click += async (_, _) => await OpenLibraryAsync(path);
                _recentButton.DropDownItems.Add(item);
            }
        }

        private static string BuildRecentLabel(string path)
        {
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path : $"{name}  ({path})";
        }

        private void RebuildKindFilter()
        {
            var selected = _kindBox.SelectedItem as string ?? "All";
            _kindBox.Items.Clear();
            _kindBox.Items.Add("All");
            foreach (var kind in ActiveAssetItems()
                .Select(x => string.IsNullOrWhiteSpace(x.ResourceKind) ? "Unknown" : x.ResourceKind)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x))
            {
                _kindBox.Items.Add(kind);
            }

            _kindBox.SelectedItem = _kindBox.Items.Contains(selected) ? selected : "All";
        }

        private IEnumerable<LibraryModelItem> ActiveAssetItems()
        {
            if (_mainTabs.SelectedTab == _texturesPage)
            {
                return _allTextures;
            }

            if (_mainTabs.SelectedTab == _vfxPage)
            {
                return _allVfx;
            }

            return _allModels;
        }

        private void RefreshListOnly()
        {
            if (_thumbnailCache == null)
            {
                return;
            }

            ResetThumbnailProgress();
            ApplyFilter();
            UpdateDetails();
            UpdateTextureDetails();
            UpdateVfxDetails();
            UpdateStatus("列表已刷新");
        }

        private void ApplyFilter()
        {
            if (_mainTabs.SelectedTab == _animationsPage)
            {
                RebuildLibraryAnimationList();
                RebuildAnimationModelList();
                UpdateStatus("动画筛选已更新");
                return;
            }

            var text = _searchBox.Text?.Trim() ?? "";
            var searchTerms = BuildSearchTerms(text);
            var kind = _kindBox.SelectedItem as string ?? "All";
            IEnumerable<LibraryModelItem> query = ActiveAssetItems();

            if (_mainTabs.SelectedTab == _modelsPage && !IsAllModelType(_selectedModelType))
            {
                query = query.Where(x => string.Equals(x.ModelSourceLabel, _selectedModelType, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.Equals(kind, "All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(x => string.Equals(x.ResourceKind, kind, StringComparison.OrdinalIgnoreCase));
            }

            if (_mainTabs.SelectedTab == _modelsPage)
            {
                query = ApplyQualityFilter(query);
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                query = query.Where(x => MatchesSearch(x, searchTerms));
            }

            if (_hideIgnoredButton.Checked && _curationStore != null)
            {
                query = query.Where(x => !_curationStore.IsIgnored(x));
            }

            if (_showFavoriteModelsButton.Checked && _curationStore != null)
            {
                query = query.Where(x => _curationStore.IsFavoriteModel(x));
            }

            if (_thumbnailCache != null && _mainTabs.SelectedTab != _texturesPage)
            {
                query = ApplyThumbnailStateFilter(query);
            }

            if (_mainTabs.SelectedTab == _texturesPage)
            {
                _visibleTextures = query.ToList();
                _textureList.VirtualListSize = _visibleTextures.Count;
                PreloadVisibleTextureThumbnails();
                _textureList.Refresh();
            }
            else if (_mainTabs.SelectedTab == _vfxPage)
            {
                _visibleVfx = query.ToList();
                _vfxList.VirtualListSize = _visibleVfx.Count;
                PreloadVisibleVfxThumbnails();
                _vfxList.Refresh();
            }
            else
            {
                _visibleModels = query.ToList();
                _modelList.VirtualListSize = _visibleModels.Count;
                PreloadVisibleThumbnails();
                _modelList.Refresh();
            }
            UpdateStatus("筛选已更新");
        }

        private void RebuildModelTypeFilter()
        {
            foreach (var button in _typeButtons)
            {
                _searchStrip.Items.Remove(button);
                button.Dispose();
            }
            _typeButtons.Clear();

            var labels = BuildModelTypeLabels();
            if (!labels.Any(x => string.Equals(x, _selectedModelType, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedModelType = "全部";
            }

            var insertIndex = _searchStrip.Items.IndexOf(_typeLabel) + 1;
            foreach (var label in labels)
            {
                var button = new ToolStripButton(label)
                {
                    DisplayStyle = ToolStripItemDisplayStyle.Text,
                    Checked = string.Equals(label, _selectedModelType, StringComparison.OrdinalIgnoreCase),
                    ToolTipText = IsAllModelType(label)
                        ? "显示所有模型类型"
                        : $"只显示 {label} 类型模型",
                };
                button.Click += (_, _) => SelectModelType(label);
                _typeButtons.Add(button);
                _searchStrip.Items.Insert(insertIndex++, button);
            }
        }

        private List<string> BuildModelTypeLabels()
        {
            var knownOrder = new[] { "全部", "Prefab", "Mesh", "Raw", "Part", "Unknown" };
            var found = _allModels
                .Select(x => x.ModelSourceLabel)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var labels = new List<string>();
            foreach (var label in knownOrder)
            {
                if (IsAllModelType(label) || found.Any(x => string.Equals(x, label, StringComparison.OrdinalIgnoreCase)))
                {
                    labels.Add(label);
                }
            }

            labels.AddRange(found
                .Where(x => !labels.Any(label => string.Equals(label, x, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            return labels;
        }

        private void SelectModelType(string label)
        {
            _selectedModelType = string.IsNullOrWhiteSpace(label) ? "全部" : label;
            foreach (var button in _typeButtons)
            {
                button.Checked = string.Equals(button.Text, _selectedModelType, StringComparison.OrdinalIgnoreCase);
            }
            ApplyFilter();
        }

        private void SwitchPrimaryTab()
        {
            UpdateToolbarForPrimaryTab();
            RebuildKindFilter();
            if (_mainTabs.SelectedTab == _animationsPage)
            {
                RebuildLibraryAnimationList();
                RebuildAnimationModelList();
            }
            else
            {
                ApplyFilter();
            }
        }

        private void UpdateToolbarForPrimaryTab()
        {
            var isModels = _mainTabs.SelectedTab == _modelsPage;
            var isAnimations = _mainTabs.SelectedTab == _animationsPage;
            var isTextures = _mainTabs.SelectedTab == _texturesPage;
            _typeLabel.Visible = isModels;
            foreach (var button in _typeButtons)
            {
                button.Visible = isModels;
            }

            _thumbnailStateBox.Visible = !isAnimations && !isTextures;
            _qualityLabel.Visible = isModels;
            _qualityBox.Visible = isModels;
            _showFavoriteModelsButton.Text = isTextures ? "收藏贴图" : isModels ? "收藏模型" : "收藏";
            _searchBox.TextBox.PlaceholderText = isAnimations
                ? "搜索动画，例如 run、attack 或 idle"
                : isTextures
                ? "搜索贴图，例如 normal 或 albedo"
                : _mainTabs.SelectedTab == _vfxPage
                ? "搜索 VFX，例如 fire 或 trail"
                : "搜索模型名称，例如 Env、player 或 tree";
        }

        private static bool IsAllModelType(string label)
        {
            return string.Equals(label, "全部", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "All", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePathForCompare(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? ""
                : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string NormalizeUnityPathForCompare(string path)
        {
            return (path ?? "").Replace('\\', '/').Trim('/');
        }

        private static HashSet<string> BuildModelBindingPathSet(LibraryModelItem model)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in model.BonePaths.Concat(model.NodePaths))
            {
                var normalized = NormalizeUnityPathForCompare(path);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                AddPathAndSuffixes(result, normalized);
                var rootIndex = normalized.IndexOf("/Root_JNT/", StringComparison.OrdinalIgnoreCase);
                if (rootIndex >= 0)
                {
                    AddPathAndSuffixes(result, normalized[(rootIndex + 1)..]);
                }
            }

            return result;
        }

        private static void AddPathAndSuffixes(HashSet<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            paths.Add(path);
            var start = path.IndexOf('/');
            while (start >= 0 && start + 1 < path.Length)
            {
                paths.Add(path[(start + 1)..]);
                start = path.IndexOf('/', start + 1);
            }
        }

        private static int CountMatchedBindingPaths(HashSet<string> modelPaths, string[] animationPaths)
        {
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in animationPaths)
            {
                var normalized = NormalizeUnityPathForCompare(path);
                if (!string.IsNullOrWhiteSpace(normalized) && modelPaths.Contains(normalized))
                {
                    matched.Add(normalized);
                }
            }

            return matched.Count;
        }

        private static bool IsTargetedAnimationModelMatch(int matchedPathCount, int animationPathCount)
        {
            if (matchedPathCount <= 0)
            {
                return false;
            }

            return animationPathCount <= 3 ? matchedPathCount == animationPathCount : matchedPathCount >= 3;
        }

        private IEnumerable<LibraryModelItem> ApplyThumbnailStateFilter(IEnumerable<LibraryModelItem> query)
        {
            return (_thumbnailStateBox.SelectedItem as string) switch
            {
                "已有缩略图" => query.Where(x => _thumbnailCache.IsCached(x)),
                "未生成" => query.Where(x => !_thumbnailCache.IsCached(x) && !_thumbnailCache.IsFailed(x)),
                "生成失败" => query.Where(x => _thumbnailCache.IsFailed(x)),
                _ => query
            };
        }

        private IEnumerable<LibraryModelItem> ApplyQualityFilter(IEnumerable<LibraryModelItem> query)
        {
            return (_qualityBox.SelectedItem as string) switch
            {
                "任务/道具" => query.Where(x => x.IsTaskOrProp),
                "任务需复查" => query.Where(x => x.IsTaskOrProp && x.NeedsReview),
                "质量问题" => query.Where(x => x.IsTaskOrProp && HasTaskModelQualityIssue(x)),
                "路径关系待补" => query.Where(x => x.RelationNeedsReview || x.IsPathOnlyTask),
                "缺材质" => query.Where(x => x.MissingMaterials),
                "无外部贴图" => query.Where(x => x.NoExternalTextureSlots),
                "验证警告" => query.Where(x => string.Equals(x.ValidationStatus, "warning", StringComparison.OrdinalIgnoreCase)),
                "有动画" => query.Where(x => _animationIndex.CountForModel(x) > 0 || x.AnimationCandidateCount > 0),
                "有导入Avatar候选" => query.Where(ModelHasImportedAvatarBakeCandidate),
                "有原始Avatar候选" => query.Where(ModelHasOriginalAvatarBakeCandidate),
                "待可信烘焙" => query.Where(ModelHasPendingTrustedUnityBake),
                "缺Avatar" => query.Where(ModelNeedsAvatarMetadata),
                "需Avatar元数据" => query.Where(ModelNeedsAvatarMetadata),
                _ => query,
            };
        }

        private bool ModelHasImportedAvatarBakeCandidate(LibraryModelItem item)
        {
            return ModelHasAnimation(item, animation =>
                animation.IsExplicit
                && HasImportedAvatarAssetOracle(animation));
        }

        private bool ModelHasOriginalAvatarBakeCandidate(LibraryModelItem item)
        {
            return ModelHasAnimation(item, animation =>
                animation.IsExplicit
                && HasOriginalAvatarOracle(animation));
        }

        private bool ModelHasPendingTrustedUnityBake(LibraryModelItem item)
        {
            return ModelHasAnimation(item, animation =>
                !animation.IsUnreal
                && animation.IsExplicit
                && RequiresUnityBake(animation)
                && !NeedsAvatarHumanDescriptionRefresh(animation)
                && !IsUnityBakeAlreadyProcessedTerminal(item, animation));
        }

        private bool ModelNeedsAvatarMetadata(LibraryModelItem item)
        {
            return ModelHasAnimation(item, animation =>
                HasMissingProductionAvatarOracle(animation));
        }

        private bool ModelHasAnimation(LibraryModelItem item, Func<LibraryAnimationCandidate, bool> predicate)
        {
            if (item == null || predicate == null)
            {
                return false;
            }

            return _animationIndex.FindForModel(item).Any(predicate);
        }

        private static bool HasTaskModelQualityIssue(LibraryModelItem item)
        {
            return item.MissingMaterials
                || item.NoExternalTextureSlots
                || string.Equals(item.ValidationStatus, "warning", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.ValidationStatus, "error", StringComparison.OrdinalIgnoreCase);
        }

        private static bool Contains(string value, string text)
        {
            return value?.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                ClearSearch();
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                ApplyFilter();
                e.SuppressKeyPress = true;
            }
        }

        private void ClearSearch()
        {
            if (string.IsNullOrEmpty(_searchBox.Text))
            {
                return;
            }

            _searchBox.Clear();
        }

        private static List<SearchTerm> BuildSearchTerms(string text)
        {
            var terms = new List<SearchTerm>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return terms;
            }

            foreach (Match match in Regex.Matches(text, "\"([^\"]+)\"|'([^']+)'|(\\S+)"))
            {
                var raw = match.Groups[1].Success
                    ? match.Groups[1].Value
                    : match.Groups[2].Success
                        ? match.Groups[2].Value
                        : match.Groups[3].Value;

                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var exclude = raw.StartsWith("-", StringComparison.Ordinal) && raw.Length > 1;
                if (exclude)
                {
                    raw = raw[1..];
                }

                raw = raw.Trim();
                if (raw.Length == 0)
                {
                    continue;
                }

                terms.Add(new SearchTerm(raw, exclude, CreateWildcardRegex(raw)));
            }

            return terms;
        }

        private static Regex CreateWildcardRegex(string pattern)
        {
            if (!HasWildcard(pattern))
            {
                return null;
            }

            // 搜索框按资源管理器习惯处理，* 表示任意字符，? 表示单个字符。
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool HasWildcard(string value)
        {
            return value.IndexOf('*') >= 0 || value.IndexOf('?') >= 0;
        }

        private bool MatchesSearch(LibraryModelItem item, List<SearchTerm> terms)
        {
            if (terms.Count == 0)
            {
                return true;
            }

            foreach (var term in terms)
            {
                var matched = SearchNameValues(item).Any(value => term.IsMatch(value));
                if (term.Exclude ? matched : !matched)
                {
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<string> SearchNameValues(LibraryModelItem item)
        {
            // 搜索只查用户肉眼看到的名称，避免 Env 命中 Environment 分类或 Models/Environment 路径。
            return Values(item.Name, item.FileName);
        }

        private static IEnumerable<string> Values(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }

        private sealed class SearchTerm
        {
            public SearchTerm(string pattern, bool exclude, Regex wildcardRegex)
            {
                Pattern = pattern;
                Exclude = exclude;
                WildcardRegex = wildcardRegex;
            }

            public string Pattern { get; }
            public bool Exclude { get; }
            public Regex WildcardRegex { get; }

            public bool IsMatch(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                return WildcardRegex != null
                    ? WildcardRegex.IsMatch(value)
                    : Contains(value, Pattern);
            }
        }

        private void ModelList_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var item = _visibleModels[e.ItemIndex];
            var imageIndex = GetImageIndex(item);
            var animations = _animationIndex.FindForModel(item);
            var animationCount = animations.Count(x => x.IsUsableCandidate);
            var allAnimationCount = animations.Count;
            var explicitCount = animations.Count(x => x.IsExplicit);
            var bakeStats = BuildModelAnimationBakeStats(item, animations);
            e.Item = new ListViewItem(ShortLabel(item, animationCount, item.AnimationCandidateCount, explicitCount, bakeStats, _curationStore?.IsFavoriteModel(item) == true))
            {
                ImageIndex = imageIndex,
                ToolTipText =
                    $"{item.OutputPath}{Environment.NewLine}" +
                    $"类型: {item.ModelSourceLabel}{Environment.NewLine}" +
                    $"动画数量: {animationCount}{Environment.NewLine}" +
                    $"关系动画总数: {allAnimationCount}{Environment.NewLine}" +
                    $"覆盖报告候选: {item.AnimationCandidateCount}{Environment.NewLine}" +
                    $"显式关系动画: {explicitCount}{Environment.NewLine}" +
                    bakeStats.ToTooltipText() +
                    $"质量复查: {(item.NeedsReview ? FormatReasonList(item.ReviewReasons) : "否")}{Environment.NewLine}" +
                    $"关系待补: {(item.RelationNeedsReview ? FormatReasonList(item.RelationReviewReasons) : "否")}"
            };
        }

        private void VfxList_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var item = _visibleVfx[e.ItemIndex];
            e.Item = new ListViewItem(ShortLabel(item, 0, 0, 0, null, _curationStore?.IsFavoriteModel(item) == true))
            {
                ImageIndex = GetImageIndex(item),
                ToolTipText = $"{item.OutputPath}{Environment.NewLine}类型: VFX/{item.VfxCategory}{Environment.NewLine}组件: {item.ComponentCount} | 材质: {item.MaterialRefCount} | 贴图: {item.TextureRefCount} | Mesh: {item.MeshRefCount} | 出现: {item.OccurrenceCount}"
            };
        }

        private void TextureList_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var item = _visibleTextures[e.ItemIndex];
            e.Item = new ListViewItem(ShortLabel(item, 0, 0, 0, null, _curationStore?.IsFavoriteModel(item) == true))
            {
                ImageIndex = GetTextureImageIndex(item),
                ToolTipText = $"{item.OutputPath}{Environment.NewLine}类型: {item.ResourceKind}/{item.SourceType}"
            };
        }

        private void ModelList_CacheVirtualItems(object sender, CacheVirtualItemsEventArgs e)
        {
            PreloadThumbnailRange(e.StartIndex, e.EndIndex);
        }

        private void VfxList_CacheVirtualItems(object sender, CacheVirtualItemsEventArgs e)
        {
            PreloadVfxThumbnailRange(e.StartIndex, e.EndIndex);
        }

        private void TextureList_CacheVirtualItems(object sender, CacheVirtualItemsEventArgs e)
        {
            PreloadTextureThumbnailRange(e.StartIndex, e.EndIndex);
        }

        private int GetImageIndex(LibraryModelItem item)
        {
            if (_curationStore?.IsIgnored(item) == true)
            {
                return GetExistingImageIndex("ignored");
            }

            var key = item.StableKey;
            if (_images.Images.ContainsKey(key))
            {
                return GetExistingImageIndex(key);
            }

            if (_thumbnailCache != null && _thumbnailCache.TryLoadExisting(item, out var image))
            {
                _images.Images.Add(key, image);
                return GetExistingImageIndex(key);
            }

            return GetExistingImageIndex("placeholder");
        }

        private int GetExistingImageIndex(string key)
        {
            var index = _images.Images.IndexOfKey(key);
            return index >= 0 ? index : 0;
        }

        private int GetTextureImageIndex(LibraryModelItem item)
        {
            var key = item.StableKey;
            if (_textureImages.Images.ContainsKey(key))
            {
                return _textureImages.Images.IndexOfKey(key);
            }

            QueueTextureThumbnail(item);
            return _textureImages.Images.IndexOfKey("texture-placeholder");
        }

        private static Image TryCreateTextureThumbnail(string path, Size imageSize)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                using var source = Image.FromFile(path);
                var bitmap = new Bitmap(imageSize.Width, imageSize.Height);
                using var g = Graphics.FromImage(bitmap);
                g.Clear(Color.FromArgb(31, 34, 38));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                var scale = Math.Min((float)bitmap.Width / source.Width, (float)bitmap.Height / source.Height);
                var width = Math.Max(1, (int)(source.Width * scale));
                var height = Math.Max(1, (int)(source.Height * scale));
                var x = (bitmap.Width - width) / 2;
                var y = (bitmap.Height - height) / 2;
                g.DrawImage(source, new Rectangle(x, y, width, height));
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private void QueueTextureThumbnail(LibraryModelItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.StableKey))
            {
                return;
            }

            var key = item.StableKey;
            if (_textureImages.Images.ContainsKey(key))
            {
                return;
            }

            lock (_queuedTextureThumbnails)
            {
                if (!_queuedTextureThumbnails.Add(key))
                {
                    return;
                }
            }

            var outputPath = item.OutputPath;
            var imageSize = _textureImages.ImageSize;
            _ = Task.Run(async () =>
            {
                await _textureThumbnailSlots.WaitAsync().ConfigureAwait(false);
                Image image = null;
                try
                {
                    image = TryCreateTextureThumbnail(outputPath, imageSize);
                }
                finally
                {
                    _textureThumbnailSlots.Release();
                }

                if (image == null)
                {
                    return;
                }

                if (IsDisposed || !IsHandleCreated)
                {
                    image.Dispose();
                    return;
                }

                BeginInvoke(() =>
                {
                    if (_textureImages.Images.ContainsKey(key))
                    {
                        image.Dispose();
                        return;
                    }

                    _textureImages.Images.Add(key, image);
                    var index = _visibleTextures.FindIndex(x => string.Equals(x.StableKey, key, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0 && index < _textureList.VirtualListSize)
                    {
                        try
                        {
                            _textureList.RedrawItems(index, index, false);
                        }
                        catch
                        {
                            _textureList.Refresh();
                        }
                    }
                });
            });
        }

        private void PreloadVisibleThumbnails()
        {
            if (_visibleModels.Count == 0)
            {
                return;
            }

            var start = 0;
            try
            {
                start = _modelList.TopItem?.Index ?? 0;
            }
            catch
            {
                start = 0;
            }

            PreloadThumbnailRange(start, Math.Min(_visibleModels.Count - 1, start + 220));
        }

        private void PreloadVisibleVfxThumbnails()
        {
            if (_visibleVfx.Count == 0)
            {
                return;
            }

            var start = 0;
            try
            {
                start = _vfxList.TopItem?.Index ?? 0;
            }
            catch
            {
                start = 0;
            }

            PreloadVfxThumbnailRange(start, Math.Min(_visibleVfx.Count - 1, start + 220));
        }

        private void PreloadVisibleTextureThumbnails()
        {
            if (_visibleTextures.Count == 0)
            {
                return;
            }

            var start = 0;
            try
            {
                start = _textureList.TopItem?.Index ?? 0;
            }
            catch
            {
                start = 0;
            }

            PreloadTextureThumbnailRange(start, Math.Min(_visibleTextures.Count - 1, start + 220));
        }

        private void PreloadThumbnailRange(int startIndex, int endIndex)
        {
            if (_thumbnailCache == null || _visibleModels.Count == 0)
            {
                return;
            }

            startIndex = Math.Max(0, startIndex);
            endIndex = Math.Min(_visibleModels.Count - 1, endIndex + 80);
            for (var i = startIndex; i <= endIndex; i++)
            {
                var item = _visibleModels[i];
                if (_curationStore?.IsIgnored(item) == true || _images.Images.ContainsKey(item.StableKey))
                {
                    continue;
                }

                if (_thumbnailCache.TryLoadExisting(item, out var image))
                {
                    _images.Images.Add(item.StableKey, image);
                }
            }
        }

        private void PreloadVfxThumbnailRange(int startIndex, int endIndex)
        {
            if (_thumbnailCache == null || _visibleVfx.Count == 0)
            {
                return;
            }

            startIndex = Math.Max(0, startIndex);
            endIndex = Math.Min(_visibleVfx.Count - 1, endIndex + 80);
            for (var i = startIndex; i <= endIndex; i++)
            {
                var item = _visibleVfx[i];
                if (_curationStore?.IsIgnored(item) == true || _images.Images.ContainsKey(item.StableKey))
                {
                    continue;
                }

                if (_thumbnailCache.TryLoadExisting(item, out var image))
                {
                    _images.Images.Add(item.StableKey, image);
                }
            }
        }

        private void PreloadTextureThumbnailRange(int startIndex, int endIndex)
        {
            if (_visibleTextures.Count == 0)
            {
                return;
            }

            startIndex = Math.Max(0, startIndex);
            endIndex = Math.Min(_visibleTextures.Count - 1, endIndex + 24);
            for (var i = startIndex; i <= endIndex; i++)
            {
                var item = _visibleTextures[i];
                if (!_textureImages.Images.ContainsKey(item.StableKey))
                {
                    QueueTextureThumbnail(item);
                }
            }
        }

        private static string ShortLabel(LibraryModelItem item, int usableAnimationCount, int reportedAnimationCount, int explicitAnimationCount, ModelAnimationBakeStats bakeStats, bool favorite)
        {
            var name = string.IsNullOrWhiteSpace(item.Name) ? item.FileName : item.Name;
            var animationBadge = BuildAnimationBadge(usableAnimationCount, reportedAnimationCount, explicitAnimationCount, bakeStats);
            var suffix = item.IsVfx
                ? $" [{(string.IsNullOrWhiteSpace(item.VfxCategory) ? "VFX" : item.VfxCategory)}]"
                : string.IsNullOrWhiteSpace(animationBadge) ? "" : $" [{animationBadge}]";
            var favoriteBadge = favorite ? " [收藏]" : "";
            var maxNameLength = Math.Max(12, 48 - suffix.Length - favoriteBadge.Length);
            var shortName = name.Length <= maxNameLength ? name : name[..Math.Max(1, maxNameLength - 3)] + "...";
            return shortName + suffix + favoriteBadge;
        }

        private static string BuildAnimationBadge(int usableAnimationCount, int reportedAnimationCount, int explicitAnimationCount, ModelAnimationBakeStats bakeStats)
        {
            if (usableAnimationCount <= 0 && reportedAnimationCount <= 0)
            {
                return "";
            }

            var explicitText = explicitAnimationCount > 0 ? $" 显式{explicitAnimationCount}" : "";
            var bakeText = bakeStats?.UnityBakeRequired > 0
                ? $" 烘焙{bakeStats.UnityBaked}/{bakeStats.UnityBakeRequired}{BuildAvatarBakeBadge(bakeStats)}"
                : "";
            if (reportedAnimationCount > 0 && reportedAnimationCount != usableAnimationCount)
            {
                return $"动画{usableAnimationCount}/候选{reportedAnimationCount}{explicitText}{bakeText}";
            }

            return $"动画{usableAnimationCount}{explicitText}{bakeText}";
        }

        private static string BuildAvatarBakeBadge(ModelAnimationBakeStats bakeStats)
        {
            if (bakeStats == null || bakeStats.UnityBakeWithAvatarAsset <= 0)
            {
                return bakeStats?.UnityBakeWithoutAvatarOracle > 0
                    ? $" 缺A{bakeStats.UnityBakeWithoutAvatarOracle}"
                    : "";
            }

            var missing = bakeStats.UnityBakeWithoutAvatarOracle > 0
                ? $" 缺A{bakeStats.UnityBakeWithoutAvatarOracle}"
                : "";
            return $" Avatar{bakeStats.UnityBakeWithAvatarAsset}{missing}";
        }

        private sealed class ModelAnimationBakeStats
        {
            public int UnityBakeRequired { get; set; }
            public int UnityBakeWithAvatarAsset { get; set; }
            public int UnityBakeWithModelAvatar { get; set; }
            public int UnityBakeWithoutAvatarOracle { get; set; }
            public int UnityBaked { get; set; }
            public int UnityBakePending { get; set; }
            public int DirectPreview { get; set; }
            public int OldLocalPreview { get; set; }
            public int StaticPose { get; set; }
            public int NeedsManualReview { get; set; }
            public int NeedRebuild { get; set; }
            public int Failed { get; set; }

            public string ToDetailText()
            {
                if (UnityBakeRequired == 0 && DirectPreview == 0)
                {
                    return "";
                }

                return
                    $"主线 Unity 烘焙需求: {UnityBakeRequired}{Environment.NewLine}" +
                    $"导入 Avatar asset: {UnityBakeWithAvatarAsset}{Environment.NewLine}" +
                    $"原始模型 Avatar/HumanDescription: {UnityBakeWithModelAvatar}{Environment.NewLine}" +
                    $"缺生产 Avatar oracle: {UnityBakeWithoutAvatarOracle}{Environment.NewLine}" +
                    $"主线已 Unity 烘焙: {UnityBaked}{Environment.NewLine}" +
                    $"主线待 Unity 烘焙: {UnityBakePending}{Environment.NewLine}" +
                    $"Unity 烘焙静态姿态: {StaticPose}{Environment.NewLine}" +
                    $"Unity 烘焙需人工验收: {NeedsManualReview}{Environment.NewLine}" +
                    $"Unity 烘焙需重建: {NeedRebuild}{Environment.NewLine}" +
                    $"Unity 烘焙失败: {Failed}{Environment.NewLine}" +
                    $"可直接 glTF 预览: {DirectPreview}{Environment.NewLine}" +
                    $"旧内部求解/局部预览: {OldLocalPreview}{Environment.NewLine}";
            }

            public string ToTooltipText()
            {
                if (UnityBakeRequired == 0 && DirectPreview == 0)
                {
                    return "";
                }

                return
                    $"主线 Unity 烘焙: {UnityBaked}/{UnityBakeRequired}{Environment.NewLine}" +
                    $"导入 Avatar asset: {UnityBakeWithAvatarAsset} | 原始模型 Avatar/HumanDescription: {UnityBakeWithModelAvatar} | 缺 oracle: {UnityBakeWithoutAvatarOracle}{Environment.NewLine}" +
                    $"待烘焙: {UnityBakePending} | 静态姿态: {StaticPose} | 人工验收: {NeedsManualReview} | 需重建: {NeedRebuild} | 失败: {Failed}{Environment.NewLine}" +
                    $"可直接 glTF 预览: {DirectPreview}{Environment.NewLine}";
            }
        }

        private sealed class AnimationModelPreviewStatus
        {
            public AnimationModelPreviewStatus(string label, string tooltip)
            {
                Label = label ?? "";
                Tooltip = tooltip ?? "";
            }

            public string Label { get; }
            public string Tooltip { get; }
        }

        private static void SetLargeIconSpacing(ListView list)
        {
            // LargeIcon 没有公开的格子宽度属性，只能用原生消息调图标间距。
            if (list.IsHandleCreated)
            {
                SendMessage(list.Handle, LvmSetIconSpacing, IntPtr.Zero, MakeLParam(LargeIconCellWidth, LargeIconCellHeight));
            }
        }

        private static IntPtr MakeLParam(int low, int high)
        {
            return (IntPtr)((high << 16) | (low & 0xffff));
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private void StartThumbnailQueue(CancellationToken cancellationToken)
        {
            if (_thumbnailCache?.HasPersistentRenderer != true && _thumbnailCache?.HasF3d != true)
            {
                UpdateStatus("没有可用缩略图渲染器");
                return;
            }

            var pendingItems = _allModels.Concat(_allVfx)
                .Where(x => !_thumbnailCache.IsCached(x) && !_thumbnailCache.IsFailed(x))
                .ToList();
            Interlocked.Exchange(ref _thumbnailPending, pendingItems.Count);
            Interlocked.Exchange(ref _thumbnailActive, 0);
            UpdateStatus("缩略图后台生成中");

            var nextIndex = -1;
            var workerCount = Math.Min(GetThumbnailConcurrency(), Math.Max(1, pendingItems.Count));
            _ = Task.Run(async () =>
            {
                var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var index = Interlocked.Increment(ref nextIndex);
                        if (index >= pendingItems.Count)
                        {
                            break;
                        }

                        var item = pendingItems[index];
                        Interlocked.Decrement(ref _thumbnailPending);
                        Interlocked.Increment(ref _thumbnailActive);
                        try
                        {
                            await RenderOneThumbnailAsync(item, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _thumbnailActive);
                            RequestUiRefresh(false);
                        }
                    }
                }, cancellationToken));

                try
                {
                    await Task.WhenAll(workers).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    _thumbnailCache?.StopRenderWorkers();
                    SafeBeginInvoke(() => UpdateStatus("缩略图队列完成，worker 已关闭"));
                }
            }, cancellationToken);
        }

        private async Task RenderOneThumbnailAsync(LibraryModelItem item, CancellationToken cancellationToken)
        {
            var path = await _thumbnailCache.EnsureAsync(item, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || cancellationToken.IsCancellationRequested)
            {
                if (_thumbnailCache.IsFailed(item))
                {
                    Interlocked.Increment(ref _thumbnailFailed);
                }

                return;
            }

            Interlocked.Increment(ref _thumbnailCached);
            RequestUiRefresh(true);
        }

        private void RequestUiRefresh(bool refreshList)
        {
            if (refreshList)
            {
                _listRefreshRequested = true;
            }

            _statusRefreshRequested = true;
        }

        private void FlushQueuedUiRefresh()
        {
            if (_listRefreshRequested)
            {
                PreloadVisibleThumbnails();
                _modelList.Refresh();
                PreloadVisibleVfxThumbnails();
                _vfxList.Refresh();
                _listRefreshRequested = false;
            }

            if (_statusRefreshRequested)
            {
                UpdateStatus("缩略图后台生成中");
                _statusRefreshRequested = false;
            }
        }

        private void SafeBeginInvoke(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // 窗口正在关闭时可能已经无法投递 UI 消息。
            }
        }

        private void RestartThumbnailQueue()
        {
            if (string.IsNullOrWhiteSpace(_root) || (_allModels.Count == 0 && _allVfx.Count == 0))
            {
                return;
            }

            _thumbnailCts?.Cancel();
            _thumbnailCache?.Dispose();
            _thumbnailCts = new CancellationTokenSource();
            _thumbnailCache = new ThumbnailCache(_root, GetThumbnailConcurrency());
            ResetThumbnailProgress();
            StartThumbnailQueue(_thumbnailCts.Token);
            UpdateStatus("已按新并发重启缩略图队列");
        }

        private int GetThumbnailConcurrency()
        {
            return int.TryParse(_concurrencyBox.SelectedItem as string, out var value) ? value : 2;
        }

        private void ResetThumbnailProgress()
        {
            if (_thumbnailCache == null)
            {
                _thumbnailTotal = _allModels.Count + _allVfx.Count;
                _thumbnailCached = 0;
                _thumbnailFailed = 0;
                _thumbnailPending = 0;
                _thumbnailActive = 0;
                return;
            }

            var thumbnailItems = _allModels.Concat(_allVfx).ToList();
            _thumbnailTotal = thumbnailItems.Count;
            _thumbnailCached = thumbnailItems.Count(_thumbnailCache.IsCached);
            _thumbnailFailed = thumbnailItems.Count(_thumbnailCache.IsFailed);
            _thumbnailPending = Math.Max(0, _thumbnailTotal - _thumbnailCached - _thumbnailFailed);
            _thumbnailActive = 0;
        }

        private void UpdateStatus(string message)
        {
            var pending = Math.Max(0, Volatile.Read(ref _thumbnailPending));
            var active = Math.Max(0, Volatile.Read(ref _thumbnailActive));
            var renderer = _thumbnailCache?.HasPersistentRenderer == true ? "常驻GL" : "无内置渲染";
            var fallback = _thumbnailCache?.HasF3d == true ? "f3d fallback" : "无f3d";
            var shown = _mainTabs.SelectedTab == _texturesPage
                ? $"{_visibleTextures.Count}/{_allTextures.Count} 贴图"
                : _mainTabs.SelectedTab == _vfxPage
                ? $"{_visibleVfx.Count}/{_allVfx.Count} VFX"
                : _mainTabs.SelectedTab == _animationsPage
                ? $"{_visibleLibraryAnimations.Count}/{_allLibraryAnimations.Count} 动画"
                : $"{_visibleModels.Count}/{_allModels.Count} 模型";
            _statusLabel.Text =
                $"{message} | 显示 {shown} | " +
                $"缩略图 {Volatile.Read(ref _thumbnailCached)}/{_thumbnailTotal} | " +
                $"失败 {Volatile.Read(ref _thumbnailFailed)} | 队列 {pending} | 运行 {active} | 并发 {GetThumbnailConcurrency()}";
            _statusLabel.Text += $" | {_deterministicAnimationSummary.ShortLabel()} | {_bakeCacheSummary.ShortLabel()} | {renderer} | {fallback}";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _uiRefreshTimer.Stop();
            _unityWorkerStatusTimer.Stop();
            _thumbnailCts?.Cancel();
            _thumbnailCache?.Dispose();
            base.OnFormClosed(e);
        }

        private void UpdateDetails()
        {
            var requestId = ++_detailRequestId;
            var item = SelectedItems().FirstOrDefault();
            if (item == null)
            {
                _detailBox.Clear();
                _detailModel = null;
                _detailAnimations.Clear();
                _animationList.Items.Clear();
                return;
            }

            var animations = _animationIndex.FindForModel(item);
            _detailModel = item;
            _detailAnimations = animations.ToList();
            RebuildAnimationList();
            WriteModelDetailText(item, animations);

            // 默认动画列表只展示 Unity/引擎索引里的确定性关系。
            // 结构 targeted 匹配只能作为显式诊断入口，不能自动混进用户看到的候选动画。
        }

        private void WriteModelDetailText(LibraryModelItem item, IReadOnlyList<LibraryAnimationCandidate> animations)
        {
            var usableCount = animations.Count(x => x.IsUsableCandidate);
            var allAnimationCount = animations.Count;
            var explicitCount = animations.Count(x => x.IsExplicit);
            var animationIndexNote = BuildModelAnimationIndexNote(item, usableCount, allAnimationCount, explicitCount);
            var animationBakeSummary = BuildModelAnimationBakeSummary(item, animations);
            _detailBox.Text =
                $"名称: {item.Name}{Environment.NewLine}" +
                $"模型来源: {item.ModelSourceLabel}{Environment.NewLine}" +
                $"分类: {item.ResourceKind}{Environment.NewLine}" +
                $"角色: {item.LibraryRole}{Environment.NewLine}" +
                $"来源类型: {item.SourceType}{Environment.NewLine}" +
                $"来源文件: {item.Source}{Environment.NewLine}" +
                $"对象路径: {EmptyAsNone(item.ObjectPath)}{Environment.NewLine}" +
                $"PathID: {item.PathId}{Environment.NewLine}" +
                $"Mesh: {item.MeshCount}{Environment.NewLine}" +
                $"顶点: {item.VertexCount}{Environment.NewLine}" +
                $"材质: {item.MaterialCount}{Environment.NewLine}" +
                $"贴图: {item.TextureCount}{Environment.NewLine}" +
                $"骨骼: {item.BoneCount}{Environment.NewLine}" +
                $"UE骨骼模型: {(item.HasSkin ? "是" : "否")}{Environment.NewLine}" +
                $"UE Skeleton路径: {(item.HasSkeletonPath ? "有" : "无")}{Environment.NewLine}" +
                $"UE组件引用: {item.ComponentReferenceCount}{Environment.NewLine}" +
                $"UE源索引对象: {item.SourceIndexObjectCount}{Environment.NewLine}" +
                $"UE任务/道具: {(item.IsTaskOrProp ? "是" : "否")}{Environment.NewLine}" +
                $"UE质量复查: {(item.NeedsReview ? "是" : "否")} {FormatReasonList(item.ReviewReasons)}{Environment.NewLine}" +
                $"UE关系待补: {(item.RelationNeedsReview ? "是" : "否")} {FormatReasonList(item.RelationReviewReasons)}{Environment.NewLine}" +
                $"UE纯路径推断任务: {(item.IsPathOnlyTask ? "是" : "否")}{Environment.NewLine}" +
                $"UE缺材质: {(item.MissingMaterials ? "是" : "否")}{Environment.NewLine}" +
                $"UE缺外部贴图槽: {(item.NoExternalTextureSlots ? "是" : "否")}{Environment.NewLine}" +
                $"动画索引来源: {EmptyAsUnknown(_animationIndex.LoadSource)}{Environment.NewLine}" +
                _sourceIndexHealth.DetailText() +
                _deterministicAnimationSummary.DetailText() +
                _bakeCacheSummary.DetailText() +
                $"动画数量: {usableCount}{Environment.NewLine}" +
                $"关系动画总数: {allAnimationCount}{Environment.NewLine}" +
                $"显式关系动画: {explicitCount}{Environment.NewLine}" +
                animationBakeSummary +
                $"覆盖报告动画候选: {item.AnimationCandidateCount}{Environment.NewLine}" +
                animationIndexNote +
                $"任务/交互信号: {FormatSignalList(item.TaskSignals)}{Environment.NewLine}" +
                $"已忽略: {_curationStore?.IsIgnored(item)}{Environment.NewLine}" +
                $"已收藏: {_curationStore?.IsFavoriteModel(item)}{Environment.NewLine}" +
                $"文件:{Environment.NewLine}{item.OutputPath}{Environment.NewLine}{Environment.NewLine}" +
                $"动画提示: 上方列表是当前模型匹配到的动画；双击动画可生成模型+动画的可播放 glTF 预览。动画页则用于从动画反查可匹配模型。{Environment.NewLine}{Environment.NewLine}" +
                BuildAnimationDetails(animations);
        }

        private string BuildModelAnimationBakeSummary(
            LibraryModelItem item,
            IReadOnlyList<LibraryAnimationCandidate> animations)
        {
            return BuildModelAnimationBakeStats(item, animations).ToDetailText();
        }

        private ModelAnimationBakeStats BuildModelAnimationBakeStats(
            LibraryModelItem item,
            IReadOnlyList<LibraryAnimationCandidate> animations)
        {
            var stats = new ModelAnimationBakeStats();
            if (item == null || animations == null || animations.Count == 0)
            {
                return stats;
            }

            foreach (var animation in animations)
            {
                if (animation.IsUnreal || !animation.IsUsableCandidate)
                {
                    continue;
                }

                var status = _previewCache?.GetStatus(item, animation);
                var playable = string.Equals(status?.Status, "可播放", StringComparison.OrdinalIgnoreCase);
                if (RequiresUnityBake(animation))
                {
                    // 模型卡片和详情里的“主线 Unity 烘焙/缺A”只统计 Unity 显式关系。
                    // 诊断或人工预览候选不能混进默认可烘焙进度，避免看起来像确定性绑定。
                    if (!animation.IsExplicit)
                    {
                        continue;
                    }

                    stats.UnityBakeRequired++;
                    if (string.Equals(animation.ProductionUnityBakeAvatarSource, "imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.UnityBakeWithAvatarAsset++;
                    }
                    else if (IsKnownProductionAvatarSource(animation))
                    {
                        stats.UnityBakeWithModelAvatar++;
                    }
                    else if (HasMissingProductionAvatarOracle(animation))
                    {
                        stats.UnityBakeWithoutAvatarOracle++;
                    }

                    if (playable)
                    {
                        stats.UnityBaked++;
                    }
                    else if (string.Equals(status?.Status, "静态姿态", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.StaticPose++;
                    }
                    else if (string.Equals(status?.Status, "需人工验收", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.NeedsManualReview++;
                    }
                    else if (string.Equals(status?.Status, "已烘焙但需重建", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.NeedRebuild++;
                    }
                    else if (string.Equals(status?.Status, "烘焙失败", StringComparison.OrdinalIgnoreCase))
                    {
                        stats.Failed++;
                    }
                    else
                    {
                        stats.UnityBakePending++;
                        if (string.Equals(status?.Status, "局部预览", StringComparison.OrdinalIgnoreCase))
                        {
                            stats.OldLocalPreview++;
                        }
                    }

                    continue;
                }

                // 非 Humanoid/Muscle 的 Transform/BlendShape 动画仍可走直接 glTF 预览。
                if (animation.MatchedPathCount > 0 || playable)
                {
                    stats.DirectPreview++;
                }
            }

            return stats;
        }

        private void RefreshCurrentModelDetailText()
        {
            if (_detailModel == null || _detailAnimations.Count == 0)
            {
                return;
            }

            WriteModelDetailText(_detailModel, _detailAnimations);
        }

        private static string BuildAnimationDetails(IReadOnlyList<LibraryAnimationCandidate> animations)
        {
            if (animations.Count == 0)
            {
                return "显式关系动画: 0\r\n当前索引没有记录 Unity Animator/Animation 或 UE 组件/骨骼关系绑定。";
            }

            var explicitCount = animations.Count(x => x.IsExplicit);
            var lines = new List<string>
            {
                $"显式关系动画: {explicitCount}",
                $"关系动画总数: {animations.Count}"
            };

            foreach (var animation in animations.Take(24))
            {
                var score = animation.Score > 0 ? $" score={animation.Score:0.###}" : "";
                var confidence = string.IsNullOrWhiteSpace(animation.Confidence) ? "" : $" {animation.Confidence}";
                var capability = string.IsNullOrWhiteSpace(animation.Capability) ? "" : $" {animation.Capability}";
                var bake = RequiresUnityBake(animation) ? " 需要Unity烘焙" : "";
                var avatarSource = FormatProductionAvatarSource(animation);
                avatarSource = string.IsNullOrWhiteSpace(avatarSource) ? "" : $" AvatarOracle={avatarSource}";
                var avatarAsset = string.IsNullOrWhiteSpace(animation.ProductionUnityBakeAvatarAsset) ? "" : $" AvatarAsset={animation.ProductionUnityBakeAvatarAsset}";
                var avatarKey = string.IsNullOrWhiteSpace(animation.ProductionUnityBakeAvatarMatchKey) ? "" : $" AvatarKey={animation.ProductionUnityBakeAvatarMatchKey}";
                var validation = FormatAnimationValidation(animation);
                var source = animation.IsExplicit ? "显式关系" : "结构关系";
                lines.Add($"- [{source}] {animation.Name}{score}{confidence}{capability}{validation}{bake}{avatarSource}{avatarAsset}{avatarKey}");
                if (!string.IsNullOrWhiteSpace(animation.BestPath))
                {
                    lines.Add($"  {animation.BestPath}");
                }
            }

            if (animations.Count > 24)
            {
                lines.Add($"... 还有 {animations.Count - 24} 个候选未显示");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string BuildVfxDetails(LibraryModelItem item, bool favorite)
        {
            var lines = new List<string>
            {
                $"名称: {item.Name}",
                $"资产类型: VFX",
                $"VFX 分类: {EmptyAsUnknown(item.VfxCategory)}",
                $"资源分类: {EmptyAsUnknown(item.ResourceKind)}",
                $"置信度: {EmptyAsUnknown(item.Confidence)}",
                $"状态: {EmptyAsUnknown(item.Status)}",
                $"来源类型: {EmptyAsUnknown(item.SourceType)}",
                $"来源文件: {item.Source}",
                $"PathID: {item.PathId}",
                $"组件: {item.ComponentCount}",
                $"材质引用: {item.MaterialRefCount}",
                $"贴图引用: {item.TextureRefCount}",
                $"Mesh 引用: {item.MeshRefCount}",
                $"合并实例数: {item.OccurrenceCount}",
                $"已收藏: {favorite}",
                $"Mesh/glTF 预览: {EmptyAsNone(item.ModelPreviewPath)}",
                $"VFX 目录:{Environment.NewLine}{item.OutputPath}",
                $"metadata:{Environment.NewLine}{item.MetadataPath}",
                $"报告:{Environment.NewLine}{item.ReportPath}",
                "",
                "预览说明:",
                "- 当前右侧是基于 Unity 元数据、名称、分类、组件和材质/Mesh 引用生成的近似 VFX 预览，用于快速筛选效果类型。",
                "- 近似预览会区分 trail、shockwave、aura、smoke、projectile、beam、distortion 等形态，但仍不是 Unity runtime 100% 还原。",
                "- 粒子模块、ParticleSystemRenderer 渲染模式、材质贴图、shader 动画、VFX Graph 和动画事件绑定仍需后续 runtime 级还原。",
            };

            if (File.Exists(item.ReportPath))
            {
                lines.Add("");
                lines.Add("VFX_REPORT.md 摘要:");
                try
                {
                    lines.AddRange(File.ReadLines(item.ReportPath).Take(80));
                }
                catch (IOException ex)
                {
                    lines.Add("读取报告失败: " + ex.Message);
                }
            }

            return string.Join(Environment.NewLine, lines);
        }

        private void UpdateVfxDetails()
        {
            var item = SelectedVfxItems().FirstOrDefault();
            _vfxPreview.SetItem(item);
            _vfxDetailBox.Text = item == null
                ? "选择一个 VFX 后，这里会显示 Unity ParticleSystem/VFX 元数据、预览说明和报告摘要。"
                : BuildVfxDetails(item, _curationStore?.IsFavoriteModel(item) == true);
        }

        private void UpdateTextureDetails()
        {
            var item = SelectedTextureItems().FirstOrDefault();
            if (item == null)
            {
                _textureDetailBox.Text = "选择一张贴图后，这里会显示来源、格式分类和输出路径。";
                return;
            }

            _textureDetailBox.Text =
                $"名称: {item.Name}{Environment.NewLine}" +
                $"资产类型: 贴图{Environment.NewLine}" +
                $"分类: {item.ResourceKind}{Environment.NewLine}" +
                $"来源类型: {item.SourceType}{Environment.NewLine}" +
                $"来源文件: {item.Source}{Environment.NewLine}" +
                $"PathID: {item.PathId}{Environment.NewLine}" +
                $"已收藏: {_curationStore?.IsFavoriteModel(item)}{Environment.NewLine}" +
                $"文件:{Environment.NewLine}{item.OutputPath}";
        }

        private static string EmptyAsUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
        }

        private static string EmptyAsNone(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(none)" : value;
        }

        private static string BuildModelAnimationIndexNote(
            LibraryModelItem item,
            int usableCount,
            int allAnimationCount,
            int explicitCount)
        {
            if (item == null || item.AnimationCandidateCount <= 0 || allAnimationCount > 0)
            {
                return "";
            }

            // UE 覆盖报告和动画索引来源不同；这里把不一致明示出来，避免误以为模型完全没有动画。
            return $"动画索引提示: 覆盖报告记录了 {item.AnimationCandidateCount} 个候选，但当前索引没有命中可预览关系。" +
                $"可用 {usableCount}，显式 {explicitCount}。{Environment.NewLine}";
        }

        private async Task LoadTargetedAnimationDetailsAsync(LibraryModelItem item, int requestId)
        {
            IReadOnlyList<LibraryAnimationCandidate> targeted;
            try
            {
                targeted = await Task.Run(() => _animationIndex.FindTargetedForModel(item));
            }
            catch (Exception ex)
            {
                if (requestId == _detailRequestId && !IsDisposed)
                {
                    BeginInvoke(() => _detailBox.AppendText($"{Environment.NewLine}定向匹配失败: {ex.Message}"));
                }
                return;
            }

            if (requestId != _detailRequestId || IsDisposed)
            {
                return;
            }

            BeginInvoke(() =>
            {
                if (requestId != _detailRequestId)
                {
                    return;
                }

                // 定向结构匹配只作为诊断文本展示，不能替换默认动画列表。
                // 默认列表必须保持 Unity/引擎显式关系，避免把骨骼路径兼容误当成生产绑定。
                _detailBox.AppendText(Environment.NewLine + BuildTargetedAnimationDetails(targeted));
            });
        }

        private void RebuildAnimationList()
        {
            _animationList.BeginUpdate();
            try
            {
                _animationList.Items.Clear();
                if (_detailModel == null)
                {
                    return;
                }

                var text = _modelAnimationFilterBox.Text?.Trim() ?? "";
                var stateFilter = _modelAnimationStateBox.SelectedItem?.ToString() ?? "全部状态";
                var hiddenDiagnosticCount = _detailAnimations.Count(x => !x.IsExplicit);
                var animations = _detailAnimations
                    .Where(x => x.IsExplicit)
                    .Where(animation =>
                    {
                        var status = GetModelAnimationDisplayStatus(_detailModel, animation);
                        if (!MatchesModelAnimationStateFilter(_detailModel, animation, status, stateFilter))
                        {
                            return false;
                        }

                        return string.IsNullOrWhiteSpace(text)
                            || Contains(animation.Name, text)
                            || Contains(animation.BestPath, text)
                            || Contains(animation.Source, text)
                            || Contains(status, text)
                            || Contains(DescribeAnimationCapability(animation), text);
                    })
                    .ToList();
                if (_showFavoriteModelAnimationsButton.Checked && _curationStore != null)
                {
                    animations = animations
                        .Where(x => _curationStore.IsFavoriteAnimation(x))
                        .ToList();
                }
                _visibleDetailAnimations = animations.Take(512).ToList();

                foreach (var animation in _visibleDetailAnimations)
                {
                    var status = GetModelAnimationDisplayStatus(_detailModel, animation);
                    var source = animation.IsUnreal
                        ? animation.IsUsableCandidate ? "UE关系" : animation.IsMetadataOnly ? "UE元数据" : "UE诊断"
                        : animation.IsExplicit ? "显式" : animation.NeedsValidation ? "需验证" : "候选";
                    var animationName = _curationStore?.IsFavoriteAnimation(animation) == true
                        ? animation.Name + " [收藏]"
                        : animation.Name;
                    var item = new ListViewItem(status)
                    {
                        Tag = animation,
                        ToolTipText = animation.BestPath
                    };
                    item.SubItems.Add(source);
                    item.SubItems.Add(animationName);
                    item.SubItems.Add(animation.Duration > 0 ? $"{animation.Duration:0.##}s" : "");
                    item.SubItems.Add(animation.MatchedPathCount > 0 ? animation.MatchedPathCount.ToString() : "");
                    item.SubItems.Add(DescribeAnimationCapability(animation));
                    _animationList.Items.Add(item);
                }

                if (hiddenDiagnosticCount > 0)
                {
                    var item = new ListViewItem("诊断隐藏")
                    {
                        ToolTipText = "结构兼容、骨骼路径或人工定向匹配候选不会进入默认预览/烘焙列表。"
                    };
                    item.SubItems.Add("诊断");
                    item.SubItems.Add($"已隐藏 {hiddenDiagnosticCount} 个非显式候选");
                    item.SubItems.Add("");
                    item.SubItems.Add("");
                    item.SubItems.Add("需要显式诊断入口查看");
                    _animationList.Items.Add(item);
                }
            }
            finally
            {
                _animationList.EndUpdate();
            }
        }

        private string GetModelAnimationDisplayStatus(LibraryModelItem model, LibraryAnimationCandidate animation)
        {
            if (animation == null)
            {
                return "未生成";
            }

            if (animation.IsUnreal)
            {
                return animation.IsUsableCandidate ? "UE可预览" : animation.IsMetadataOnly ? "UE元数据" : "UE诊断";
            }

            var preview = _previewCache?.GetStatus(model, animation);
            var status = preview?.Status ?? "未生成";
            if (animation.NeedsAnimatorControllerContext
                && string.Equals(status, "未生成", StringComparison.OrdinalIgnoreCase))
            {
                return "需 AnimatorController 上下文";
            }
            if (NeedsAvatarHumanDescriptionRefresh(animation)
                && string.Equals(status, "未生成", StringComparison.OrdinalIgnoreCase))
            {
                return "需 Avatar 元数据";
            }

            if (RequiresUnityBake(animation)
                && string.Equals(status, "未生成", StringComparison.OrdinalIgnoreCase))
            {
                return FormatUnityBakePendingStatus(animation);
            }

            return status;
        }

        private bool MatchesModelAnimationStateFilter(
            LibraryModelItem model,
            LibraryAnimationCandidate animation,
            string status,
            string stateFilter)
        {
            return stateFilter switch
            {
                "可播放" => string.Equals(status, "可播放", StringComparison.OrdinalIgnoreCase),
                "待可信烘焙" => !animation.IsUnreal
                    && RequiresUnityBake(animation)
                    && !IsUnityBakeAlreadyProcessedTerminal(model, animation)
                    && !NeedsAvatarHumanDescriptionRefresh(animation),
                "导入Avatar" => !animation.IsUnreal
                    && HasImportedAvatarAssetOracle(animation),
                "原始Avatar" => !animation.IsUnreal
                    && HasOriginalAvatarOracle(animation),
                "需Avatar元数据" => !animation.IsUnreal
                    && NeedsAvatarHumanDescriptionRefresh(animation),
                "需上下文" => !animation.IsUnreal
                    && animation.NeedsAnimatorControllerContext,
                "失败/需复查" => Contains(status, "失败")
                    || Contains(status, "需重建")
                    || Contains(status, "需复查")
                    || Contains(status, "人工验收")
                    || Contains(status, "已烘焙但需重建"),
                _ => true,
            };
        }

        private void RebuildLibraryAnimationList()
        {
            var text = _searchBox.Text?.Trim() ?? "";
            IEnumerable<LibraryAnimationUsage> query = _allLibraryAnimations;
            if (!string.IsNullOrWhiteSpace(text))
            {
                query = query.Where(x =>
                    Contains(x.Animation.Name, text)
                    || Contains(x.Animation.BestPath, text)
                    || Contains(x.Animation.Source, text));
            }
            if (_showFavoriteLibraryAnimationsButton.Checked && _curationStore != null)
            {
                query = query.Where(x => _curationStore.IsFavoriteAnimation(x));
            }

            _visibleLibraryAnimations = query
                .OrderBy(x => x.Animation.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Animation.BestPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _libraryAnimationList.BeginUpdate();
            try
            {
                _libraryAnimationList.Items.Clear();
                foreach (var usage in _visibleLibraryAnimations)
                {
                    var animation = usage.Animation;
                    var animationName = _curationStore?.IsFavoriteAnimation(usage) == true
                        ? animation.Name + " [收藏]"
                        : animation.Name;
                    var item = new ListViewItem(animationName)
                    {
                        Tag = usage,
                        ToolTipText = animation.BestPath
                    };
                    item.SubItems.Add(usage.ModelCount.ToString());
                    item.SubItems.Add(animation.Duration > 0 ? $"{animation.Duration:0.##}s" : "");
                    item.SubItems.Add(DescribeAnimationCapability(animation));
                    item.SubItems.Add(animation.BestPath);
                    _libraryAnimationList.Items.Add(item);
                }
            }
            finally
            {
                _libraryAnimationList.EndUpdate();
            }
        }

        private void SelectLibraryAnimationFilter()
        {
            _selectedLibraryAnimation = _libraryAnimationList.SelectedItems.Count > 0
                ? _libraryAnimationList.SelectedItems[0].Tag as LibraryAnimationUsage
                : null;
            RebuildAnimationModelList();
            if (_selectedLibraryAnimation != null)
            {
                UpdateStatus($"动画反查: {_selectedLibraryAnimation.Animation.Name}");
            }
        }

        private void ClearSelectedLibraryAnimation()
        {
            _selectedLibraryAnimation = null;
            _libraryAnimationList.SelectedItems.Clear();
            RebuildAnimationModelList();
            UpdateStatus("已清除动画页模型列表");
        }

        private void RebuildAnimationModelList()
        {
            var availableModels = new List<LibraryModelItem>();
            _selectedAnimationIndexedModelCount = 0;
            _selectedAnimationAvailableModelCount = 0;
            if (_selectedLibraryAnimation != null)
            {
                var modelOutputs = _animationIndex.FindModelOutputsForAnimation(_selectedLibraryAnimation)
                    .Select(NormalizePathForCompare)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                _selectedAnimationIndexedModelCount = _selectedLibraryAnimation.ModelCount;
                if (modelOutputs.Count > 0)
                {
                    availableModels = _allModels
                        .Where(x => modelOutputs.Contains(NormalizePathForCompare(x.OutputPath)))
                        .ToList();
                }
                else
                {
                    var bindingPaths = _selectedLibraryAnimation.Animation.BindingPaths;
                    availableModels = _allModels
                        .Where(x => IsTargetedAnimationModelMatch(
                            CountMatchedBindingPaths(BuildModelBindingPathSet(x), bindingPaths),
                            bindingPaths.Length))
                        .ToList();
                    _selectedAnimationIndexedModelCount = availableModels.Count;
                }
            }
            _selectedAnimationAvailableModelCount = availableModels.Count;

            IEnumerable<LibraryModelItem> query = availableModels;

            var modelFilter = _animationModelFilterBox.Text?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(modelFilter))
            {
                var animation = _selectedLibraryAnimation?.Animation;
                query = query.Where(model =>
                {
                    var previewStatus = BuildAnimationModelPreviewStatus(model, ResolveAnimationForModel(model, animation));
                    return Contains(model.Name, modelFilter)
                        || Contains(model.OutputPath, modelFilter)
                        || Contains(model.ModelSourceLabel, modelFilter)
                        || Contains(model.ResourceKind, modelFilter)
                        || Contains(previewStatus.Label, modelFilter);
                });
            }
            query = ApplyAnimationModelStateFilter(query, _selectedLibraryAnimation?.Animation);

            _visibleAnimationModels = query
                .OrderBy(x => x.ModelSourceLabel, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _animationModelList.BeginUpdate();
            try
            {
                _animationModelList.Items.Clear();
                foreach (var model in _visibleAnimationModels)
                {
                    var previewAnimation = ResolveAnimationForModel(model, _selectedLibraryAnimation.Animation);
                    var previewStatus = BuildAnimationModelPreviewStatus(model, previewAnimation);
                    var item = new ListViewItem(model.Name)
                    {
                        Tag = model,
                        ToolTipText = string.IsNullOrWhiteSpace(previewStatus.Tooltip)
                            ? model.OutputPath
                            : previewStatus.Tooltip + Environment.NewLine + model.OutputPath
                    };
                    item.SubItems.Add(model.ModelSourceLabel);
                    item.SubItems.Add(model.ResourceKind);
                    item.SubItems.Add(model.BoneCount > 0 ? model.BoneCount.ToString() : "");
                    item.SubItems.Add(previewStatus.Label);
                    item.SubItems.Add(model.OutputPath);
                    _animationModelList.Items.Add(item);
                }
            }
            finally
            {
                _animationModelList.EndUpdate();
            }

            UpdateAnimationPageDetails();
        }

        private IEnumerable<LibraryModelItem> ApplyAnimationModelStateFilter(IEnumerable<LibraryModelItem> query, LibraryAnimationCandidate animation)
        {
            return (_animationModelStateBox.SelectedItem as string) switch
            {
                "可播放" => query.Where(model => IsUnityBakeAlreadyPlayable(model, ResolveAnimationForModel(model, animation))),
                "待可信烘焙" => query.Where(model =>
                {
                    var modelAnimation = ResolveAnimationForModel(model, animation);
                    return modelAnimation != null
                        && !modelAnimation.IsUnreal
                        && RequiresUnityBake(modelAnimation)
                        && !NeedsAvatarHumanDescriptionRefresh(modelAnimation)
                        && !IsUnityBakeAlreadyProcessedTerminal(model, modelAnimation);
                }),
                "导入Avatar" => query.Where(model =>
                {
                    var modelAnimation = ResolveAnimationForModel(model, animation);
                    return modelAnimation != null
                        && HasImportedAvatarAssetOracle(modelAnimation);
                }),
                "原始Avatar" => query.Where(model =>
                {
                    var modelAnimation = ResolveAnimationForModel(model, animation);
                    return modelAnimation != null
                        && HasOriginalAvatarOracle(modelAnimation);
                }),
                "需Avatar元数据" => query.Where(model =>
                {
                    var modelAnimation = ResolveAnimationForModel(model, animation);
                    return modelAnimation != null
                        && !modelAnimation.IsUnreal
                        && NeedsAvatarHumanDescriptionRefresh(modelAnimation);
                }),
                "失败/需复查" => query.Where(model =>
                {
                    var modelAnimation = ResolveAnimationForModel(model, animation);
                    var status = BuildAnimationModelPreviewStatus(model, modelAnimation).Label;
                    return Contains(status, "失败")
                        || Contains(status, "复查")
                        || Contains(status, "人工验收")
                        || Contains(status, "静态姿态")
                        || Contains(status, "需重建");
                }),
                _ => query,
            };
        }

        private LibraryAnimationCandidate ResolveAnimationForModel(LibraryModelItem model, LibraryAnimationCandidate animation)
        {
            if (model == null || animation == null)
            {
                return animation;
            }

            var targetPath = NormalizePathForCompare(animation.BestPath);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return animation;
            }

            return _animationIndex.FindForModel(model)
                .FirstOrDefault(x => string.Equals(NormalizePathForCompare(x.BestPath), targetPath, StringComparison.OrdinalIgnoreCase))
                ?? animation;
        }

        private AnimationModelPreviewStatus BuildAnimationModelPreviewStatus(LibraryModelItem model, LibraryAnimationCandidate animation)
        {
            if (model == null || animation == null)
            {
                return new AnimationModelPreviewStatus("", "");
            }

            if (animation.IsUnreal)
            {
                return new AnimationModelPreviewStatus(animation.IsUsableCandidate ? "UE可预览" : "UE元数据", "UE 动画关系来自索引；双击模型会尝试生成 UE 预览。");
            }

            var cached = _previewCache?.GetStatus(model, animation);
            if (cached != null && !string.IsNullOrWhiteSpace(cached.Status) && !string.Equals(cached.Status, "未生成", StringComparison.OrdinalIgnoreCase))
            {
                return new AnimationModelPreviewStatus(cached.Status, cached.Message ?? cached.GltfPath ?? cached.ValidationPath ?? "");
            }

            if (IsDirectGltfTransformOnly(animation))
            {
                return new AnimationModelPreviewStatus("Transform 预览", "该显式 Unity 关系可先生成 Transform 曲线 glTF；完整 Humanoid/Muscle 生产结果仍需补齐 Avatar 后再 Unity bake。");
            }

            if (NeedsAvatarHumanDescriptionRefresh(animation))
            {
                return new AnimationModelPreviewStatus("需 Avatar 元数据", "该模型-动画关系来自显式 Unity 索引，但缺少生产 Unity bake 需要的 HumanDescription。请先刷新模型 Avatar 元数据。");
            }

            if (RequiresUnityBake(animation))
            {
                var avatarText = FormatProductionAvatarTooltip(animation);
                return new AnimationModelPreviewStatus(
                    FormatUnityBakePendingStatus(animation),
                    "该模型-动画关系来自显式 Unity 索引，Humanoid/Muscle 身体动画需双击后由 Unity bake 生成可信 glTF。" + avatarText);
            }

            return new AnimationModelPreviewStatus("可生成", "双击模型会生成模型+动画 glTF 预览。");
        }

        private void UpdateAnimationPageDetails()
        {
            if (_selectedLibraryAnimation == null)
            {
                _animationDetailBox.Text = "左侧选择一个动画后，右侧会显示索引中明确关联的模型。双击模型可生成 baked glTF 预览。";
                return;
            }

            var animation = _selectedLibraryAnimation.Animation;
            var missingOrNotExported = Math.Max(0, _selectedAnimationIndexedModelCount - _selectedAnimationAvailableModelCount);
            var visibleModelStateSummary = BuildAnimationModelStateSummary(animation);
            _animationDetailBox.Text =
                $"动画: {animation.Name}{Environment.NewLine}" +
                $"时长: {(animation.Duration > 0 ? $"{animation.Duration:0.##}s" : "Unknown")}{Environment.NewLine}" +
                $"能力: {DescribeAnimationCapability(animation)}{Environment.NewLine}" +
                $"验证: {FormatAnimationValidation(animation)}{Environment.NewLine}" +
                $"帧/轨道/片段: {FormatAnimationCounts(animation)}{Environment.NewLine}" +
                $"关联模型: 当前筛选 {_visibleAnimationModels.Count} / 当前库可见 {_selectedAnimationAvailableModelCount} / 索引记录 {_selectedAnimationIndexedModelCount}{Environment.NewLine}" +
                visibleModelStateSummary +
                $"缺失或未导出模型: {missingOrNotExported}{Environment.NewLine}" +
                $"来源: {animation.Source}{Environment.NewLine}" +
                $"路径: {animation.BestPath}{Environment.NewLine}{Environment.NewLine}" +
                "操作: 在右侧模型列表双击模型，生成模型+动画的可播放 glTF 预览。";
        }

        private string BuildAnimationModelStateSummary(LibraryAnimationCandidate animation)
        {
            if (animation == null || _visibleAnimationModels.Count == 0)
            {
                return "";
            }

            var playable = 0;
            var pendingBake = 0;
            var importedAvatar = 0;
            var originalAvatar = 0;
            var needsAvatar = 0;
            var failedOrReview = 0;
            foreach (var model in _visibleAnimationModels)
            {
                var modelAnimation = ResolveAnimationForModel(model, animation);
                var status = BuildAnimationModelPreviewStatus(model, modelAnimation).Label;
                if (string.Equals(status, "可播放", StringComparison.OrdinalIgnoreCase))
                {
                    playable++;
                }
                if (modelAnimation != null
                    && !modelAnimation.IsUnreal
                    && RequiresUnityBake(modelAnimation)
                    && !NeedsAvatarHumanDescriptionRefresh(modelAnimation)
                    && !IsUnityBakeAlreadyProcessedTerminal(model, modelAnimation))
                {
                    pendingBake++;
                }
                if (HasImportedAvatarAssetOracle(modelAnimation))
                {
                    importedAvatar++;
                }
                if (HasOriginalAvatarOracle(modelAnimation))
                {
                    originalAvatar++;
                }
                if (modelAnimation != null
                    && !modelAnimation.IsUnreal
                    && NeedsAvatarHumanDescriptionRefresh(modelAnimation))
                {
                    needsAvatar++;
                }
                if (Contains(status, "失败")
                    || Contains(status, "复查")
                    || Contains(status, "人工验收")
                    || Contains(status, "静态姿态")
                    || Contains(status, "需重建"))
                {
                    failedOrReview++;
                }
            }

            return $"当前可见模型状态: 可播放 {playable} / 待可信烘焙 {pendingBake} / 导入Avatar {importedAvatar} / 原始Avatar {originalAvatar} / 需Avatar元数据 {needsAvatar} / 失败或复查 {failedOrReview}{Environment.NewLine}";
        }

        private static string DescribeAnimationCapability(LibraryAnimationCandidate animation)
        {
            if (animation == null)
            {
                return "";
            }

            if (animation.IsUnreal)
            {
                var relationLabel = DescribeUnrealRelationSource(animation.RelationSource);
                if (!animation.IsUsableCandidate)
                {
                    if (animation.IsMetadataOnly)
                    {
                        return "UE元数据";
                    }

                    return string.IsNullOrWhiteSpace(animation.ExportStatus)
                        ? "UE诊断关系"
                        : "UE导出" + animation.ExportStatus;
                }

                if (animation.IsContainerAnimation)
                {
                    return relationLabel + " 容器动画";
                }

                if (!string.IsNullOrWhiteSpace(animation.ValidationCategory))
                {
                    return relationLabel + " " + animation.ValidationCategory;
                }

                return relationLabel + " 动画索引";
            }

            if (!string.IsNullOrWhiteSpace(animation.Capability))
            {
                if (IsDirectGltfTransformOnly(animation))
                {
                    return "Transform 预览";
                }

                if (NeedsAvatarHumanDescriptionRefresh(animation))
                {
                    return "需 Avatar 元数据";
                }
                if (animation.NeedsAnimatorControllerContext)
                {
                    return "需 AnimatorController 上下文";
                }
                if (RequiresUnityBake(animation))
                {
                    return FormatUnityBakeCapability(animation);
                }

                return animation.Capability switch
                {
                    "TransformBodyPreviewReady" => "快速预览",
                    "HumanoidBodyBakeReady" => "需 Unity 烘焙",
                    "HumanoidBodyNeedsUnityBake" => "需 Unity 烘焙",
                    "BlendShapePreviewReady" => "BlendShape",
                    "NonCharacterTransformPreviewReady" => "Transform 预览",
                    _ => animation.Capability,
                };
            }

            if (!string.IsNullOrWhiteSpace(animation.ValidationCategory))
            {
                return animation.ValidationCategory;
            }

            if (NeedsAvatarHumanDescriptionRefresh(animation))
            {
                return "需 Avatar 元数据";
            }
            if (animation.NeedsAnimatorControllerContext)
            {
                return "需 AnimatorController 上下文";
            }
            if (RequiresUnityBake(animation))
            {
                return FormatUnityBakeCapability(animation);
            }

            if (animation.MatchedPathCount > 0)
            {
                return "快速预览";
            }

            return "未知";
        }

        private static string FormatAvatarMatchKey(LibraryAnimationCandidate animation)
        {
            return string.IsNullOrWhiteSpace(animation?.ProductionUnityBakeAvatarMatchKey)
                ? ""
                : $"，匹配键: {animation.ProductionUnityBakeAvatarMatchKey}";
        }

        private static bool IsKnownProductionAvatarSource(LibraryAnimationCandidate animation)
        {
            return string.Equals(animation?.ProductionUnityBakeAvatarSource, "model_human_description", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasImportedAvatarAssetOracle(LibraryAnimationCandidate animation)
        {
            return animation != null
                && !animation.IsUnreal
                && RequiresUnityBake(animation)
                && (string.Equals(animation.ProductionUnityBakeAvatarSource, "imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(animation.ProductionUnityBakeAvatarAsset));
        }

        private static bool HasOriginalAvatarOracle(LibraryAnimationCandidate animation)
        {
            return animation != null
                && !animation.IsUnreal
                && RequiresUnityBake(animation)
                && IsKnownProductionAvatarSource(animation);
        }

        private static bool HasMissingProductionAvatarOracle(LibraryAnimationCandidate animation)
        {
            if (animation == null
                || animation.IsUnreal
                || !animation.IsExplicit
                || !RequiresUnityBake(animation))
            {
                return false;
            }

            return !string.Equals(animation.ProductionUnityBakeAvatarSource, "imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase)
                && !IsKnownProductionAvatarSource(animation);
        }

        private static string FormatProductionAvatarSource(LibraryAnimationCandidate animation)
        {
            return animation?.ProductionUnityBakeAvatarSource switch
            {
                "imported_unity_avatar_asset" => "导入AvatarAsset",
                "model_human_description" => "原始模型Avatar/HumanDescription",
                "candidate_production_avatar" => "旧候选标记(需重验)",
                _ => ""
            };
        }

        private static string FormatProductionAvatarTooltip(LibraryAnimationCandidate animation)
        {
            var source = FormatProductionAvatarSource(animation);
            if (string.IsNullOrWhiteSpace(source))
            {
                return " 尚未看到生产 Avatar oracle；如果继续失败，应先恢复原始 Avatar 或刷新 HumanDescription。";
            }

            if (string.Equals(animation?.ProductionUnityBakeAvatarSource, "imported_unity_avatar_asset", StringComparison.OrdinalIgnoreCase))
            {
                return $" 已匹配{source}: {animation.ProductionUnityBakeAvatarAsset}{FormatAvatarMatchKey(animation)}。";
            }

            return $" Avatar oracle: {source}。";
        }

        private static string FormatUnityBakePendingStatus(LibraryAnimationCandidate animation)
        {
            return animation?.ProductionUnityBakeAvatarSource switch
            {
                "imported_unity_avatar_asset" => "需 Unity 烘焙(导入Avatar)",
                "model_human_description" => "需 Unity 烘焙(原始Avatar)",
                "candidate_production_avatar" => "需恢复Avatar",
                _ => "需 Unity 烘焙"
            };
        }

        private static string FormatUnityBakeCapability(LibraryAnimationCandidate animation)
        {
            return animation?.ProductionUnityBakeAvatarSource switch
            {
                "imported_unity_avatar_asset" => "需 Unity 烘焙(导入Avatar)",
                "model_human_description" => "需 Unity 烘焙(原始Avatar)",
                "candidate_production_avatar" => "需恢复Avatar",
                _ => "需 Unity 烘焙"
            };
        }

        private static bool RequiresUnityBake(LibraryAnimationCandidate animation)
        {
            if (animation?.NeedsAnimatorControllerContext == true)
            {
                return false;
            }

            return animation?.RequiresUnityBake == true
                || animation?.RequiresInternalHumanoidSolve == true
                || animation?.ProductionUnityBakeReady == true
                || string.Equals(animation?.NextAction, "generate_unity_baked_gltf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(animation?.Capability, "HumanoidBodyBakeReady", StringComparison.OrdinalIgnoreCase)
                || string.Equals(animation?.ProductionAnimationPath, "UnityBakeToGltf", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(animation?.AnimationType)
                    && (animation.AnimationType.Contains("Humanoid", StringComparison.OrdinalIgnoreCase)
                        || animation.AnimationType.Contains("Muscle", StringComparison.OrdinalIgnoreCase)));
        }

        private static bool NeedsAvatarHumanDescriptionRefresh(LibraryAnimationCandidate animation)
        {
            if (animation == null || animation.ProductionUnityBakeReady)
            {
                return false;
            }
            if (animation.NeedsAnimatorControllerContext)
            {
                return false;
            }

            return string.Equals(animation.NextAction, "refresh_avatar_human_description", StringComparison.OrdinalIgnoreCase)
                || string.Equals(animation.ProductionAnimationPath, "NeedsUnityAvatarMetadata", StringComparison.OrdinalIgnoreCase)
                || animation.ProductionUnityBakeBlocked;
        }

        private static bool IsDirectGltfTransformOnly(LibraryAnimationCandidate animation)
        {
            return string.Equals(animation?.ProductionAnimationPath, "DirectGltfTransformOnly", StringComparison.OrdinalIgnoreCase);
        }

        private static string DescribeUnrealRelationSource(string relationSource)
        {
            return relationSource switch
            {
                "componentOwner" => "UE组件",
                "componentOwnerBlendSpaceSample" => "UE组件BlendSpace",
                "componentAnimClass" => "UE动画蓝图",
                "uniqueSkeleton" => "UE唯一骨骼",
                "sharedSkeleton" => "UE共享骨骼",
                "diagnostic" => "UE诊断",
                _ => "UE",
            };
        }

        private static string FormatAnimationValidation(LibraryAnimationCandidate animation)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(animation.ValidationStatus))
            {
                parts.Add(animation.ValidationStatus);
            }
            if (!string.IsNullOrWhiteSpace(animation.ValidationCategory)
                && !string.Equals(animation.ValidationCategory, animation.ValidationStatus, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(animation.ValidationCategory);
            }
            if (animation.TrackCoverage > 0)
            {
                parts.Add($"coverage={animation.TrackCoverage:0.##}");
            }
            if (animation.IsContainerAnimation)
            {
                parts.Add("容器动画");
            }

            return parts.Count == 0 ? "" : " " + string.Join(" ", parts);
        }

        private static string FormatAnimationCounts(LibraryAnimationCandidate animation)
        {
            var frame = animation.FrameCount > 0 ? animation.FrameCount.ToString() : "-";
            var track = animation.TrackCount > 0 ? animation.TrackCount.ToString() : "-";
            var segment = animation.SegmentCount > 0 ? animation.SegmentCount.ToString() : "-";
            return $"{frame}/{track}/{segment}";
        }

        private static string FormatSignalList(string[] signals)
        {
            return signals == null || signals.Length == 0 ? "(none)" : string.Join(", ", signals.Take(8));
        }

        private static string FormatReasonList(string[] reasons)
        {
            return reasons == null || reasons.Length == 0 ? "" : $"[{string.Join(", ", reasons.Take(8))}]";
        }

        private static string BuildTargetedAnimationDetails(IReadOnlyList<LibraryAnimationCandidate> animations)
        {
            if (animations.Count == 0)
            {
                return "可匹配动画: 0\r\n没有从当前模型骨骼/节点路径命中 AnimationClip binding。";
            }

            var lines = new List<string>
            {
                $"可匹配动画: {animations.Count} [需验证]"
            };

            foreach (var animation in animations.Take(24))
            {
                var matched = animation.MatchedPathCount > 0 ? $" matched={animation.MatchedPathCount}" : "";
                var bake = RequiresUnityBake(animation) ? " 需要Unity烘焙" : "";
                lines.Add($"- [需验证] {animation.Name}{matched} score={animation.Score:0.###}{bake}");
                if (!string.IsNullOrWhiteSpace(animation.BestPath))
                {
                    lines.Add($"  {animation.BestPath}");
                }
            }

            if (animations.Count > 24)
            {
                lines.Add($"... 还有 {animations.Count - 24} 个候选未显示");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private IEnumerable<LibraryModelItem> SelectedItems()
        {
            foreach (int index in _modelList.SelectedIndices)
            {
                if (index >= 0 && index < _visibleModels.Count)
                {
                    yield return _visibleModels[index];
                }
            }
        }

        private IEnumerable<LibraryModelItem> SelectedTextureItems()
        {
            foreach (int index in _textureList.SelectedIndices)
            {
                if (index >= 0 && index < _visibleTextures.Count)
                {
                    yield return _visibleTextures[index];
                }
            }
        }

        private IEnumerable<LibraryModelItem> SelectedVfxItems()
        {
            foreach (int index in _vfxList.SelectedIndices)
            {
                if (index >= 0 && index < _visibleVfx.Count)
                {
                    yield return _visibleVfx[index];
                }
            }
        }

        private LibraryModelItem SelectedAnimationModel()
        {
            return _animationModelList.SelectedItems.Count > 0
                ? _animationModelList.SelectedItems[0].Tag as LibraryModelItem
                : _visibleAnimationModels.FirstOrDefault();
        }

        private IEnumerable<LibraryModelItem> SelectedAnimationModels()
        {
            foreach (ListViewItem item in _animationModelList.SelectedItems)
            {
                if (item.Tag is LibraryModelItem model)
                {
                    yield return model;
                }
            }
        }

        private void OpenSelectedWithF3d()
        {
            var item = SelectedItems().FirstOrDefault();
            if (item == null)
            {
                return;
            }

            if (item.IsVfx && string.IsNullOrWhiteSpace(item.ThumbnailSourcePath))
            {
                OpenSelectedFolder();
                return;
            }

            var f3d = FindF3dForOpen();
            var startInfo = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(f3d) ? item.ThumbnailSourcePath : f3d,
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(f3d))
            {
                startInfo.ArgumentList.Add(item.ThumbnailSourcePath);
            }

            Process.Start(startInfo);
        }

        private async Task ExportSelectedModelFbxAsync()
        {
            var item = SelectedItems().FirstOrDefault();
            if (item == null || item.IsTexture || item.IsVfx || string.IsNullOrWhiteSpace(item.OutputPath) || !File.Exists(item.OutputPath))
            {
                MessageBox.Show(this, "请先选择一个已导出的 glTF/GLB 模型。", "导出 FBX 模型", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var output = ChooseFbxOutputPath(item.Name, "导出 FBX 模型");
            if (string.IsNullOrWhiteSpace(output))
            {
                return;
            }

            await ExportGltfToFbxAsync(item.OutputPath, output, skeletonOnly: false, title: "导出 FBX 模型");
        }

        private async Task ExportSelectedAnimationFbxAsync()
        {
            var model = _detailModel ?? SelectedItems().FirstOrDefault();
            var animation = SelectedAnimationCandidate();
            if (model == null || animation == null || _previewCache == null)
            {
                MessageBox.Show(this, "请先选择模型和右侧动画。", "导出 FBX 动画", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (animation.IsUnreal)
            {
                MessageBox.Show(this, "当前 FBX 动画导出先支持 Unity 动画；Unreal 动画后续单独接入。", "导出 FBX 动画", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (NeedsAvatarHumanDescriptionRefresh(animation))
            {
                MessageBox.Show(this, "该动画缺少生产 Unity bake 所需的 Avatar 元数据，不能导出可信 FBX 动画。", "导出 FBX 动画", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var inputError = ValidateAnimationPreviewInputs(model, animation);
            if (!string.IsNullOrWhiteSpace(inputError))
            {
                MessageBox.Show(this, inputError, "导出 FBX 动画", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var output = ChooseFbxOutputPath($"{model.Name}__{animation.Name}", "导出 FBX 动画(无Skin)");
            if (string.IsNullOrWhiteSpace(output))
            {
                return;
            }

            UpdateSelectedAnimationStatus("导出FBX中");
            UpdateStatus("正在 Unity bake 并导出无 Skin FBX 动画");
            var status = await _previewCache.EnsureUnityBakeAsync(
                model,
                animation,
                CancellationToken.None,
                message => BeginInvoke(() => UpdateStatus(message)));
            if (string.IsNullOrWhiteSpace(status.GltfPath) || !File.Exists(status.GltfPath))
            {
                MessageBox.Show(this, status.Message ?? "Unity bake 没有生成可用于 FBX 导出的 glTF。", "导出 FBX 动画", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                UpdateSelectedAnimationStatus(status.Status);
                return;
            }

            await ExportGltfToFbxAsync(status.GltfPath, output, skeletonOnly: true, title: "导出 FBX 动画");
            UpdateSelectedAnimationStatus(status.Status);
        }

        private string ChooseFbxOutputPath(string baseName, string title)
        {
            using var dialog = new SaveFileDialog
            {
                Title = title,
                Filter = "FBX 文件 (*.fbx)|*.fbx",
                FileName = SanitizeFileName(baseName) + ".fbx",
                DefaultExt = "fbx",
                AddExtension = true,
                OverwritePrompt = true,
            };
            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
        }

        private async Task ExportGltfToFbxAsync(string inputGltf, string outputFbx, bool skeletonOnly, string title)
        {
            var launcher = FindCliLauncher();
            if (string.IsNullOrWhiteSpace(launcher.FileName))
            {
                MessageBox.Show(this, "没有找到 AnimeStudio.CLI，无法导出 FBX。", title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                UpdateStatus(skeletonOnly ? "正在导出 skeleton-only FBX 动画" : "正在导出 FBX 模型");
                var result = await RunCliAsync(launcher, launcher.BuildFbxFromGltfArguments(inputGltf, outputFbx, skeletonOnly));
                if (result.ExitCode != 0 || !File.Exists(outputFbx))
                {
                    MessageBox.Show(this, TrimProcessOutput(result.CombinedOutput), title + "失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                UpdateStatus("FBX 导出完成");
                MessageBox.Show(this, outputFbx, title + "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{outputFbx}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, title + "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task GenerateSelectedAnimationPreviewAsync(bool openAfterGenerate)
        {
            var model = _detailModel ?? SelectedItems().FirstOrDefault();
            var animation = SelectedAnimationCandidate();
            if (model == null || animation == null || _previewCache == null)
            {
                return;
            }

            if (animation.IsUnreal)
            {
                await GenerateUnrealAnimationPreviewAsync(model, animation, openAfterGenerate, RebuildAnimationList);
                return;
            }

            if (animation.NeedsAnimatorControllerContext)
            {
                MessageBox.Show(
                    this,
                    "这个 AnimationClip 只有 root motion、附件或叠加层数据，不能脱离 AnimatorController 单独烘焙成可信身体动画。请等待后续恢复 AnimatorController 状态机/Layer/BlendTree 后再生成预览。",
                    "需要 AnimatorController 上下文",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (NeedsAvatarHumanDescriptionRefresh(animation))
            {
                MessageBox.Show(
                    this,
                    "该显式 Unity 动画关系缺少生产 Unity bake 所需的 Avatar HumanDescription。当前不能生成可信身体动画预览，请先刷新模型 Avatar 元数据。",
                    "需要 Avatar 元数据",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var inputError = ValidateAnimationPreviewInputs(model, animation);
            if (!string.IsNullOrWhiteSpace(inputError))
            {
                MessageBox.Show(this, inputError, "动画预览", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var needsUnityBake = RequiresUnityBake(animation);
            UpdateSelectedAnimationStatus(needsUnityBake ? "Unity 烘焙中" : "生成中");
            UpdateStatus(needsUnityBake ? "正在执行 Unity Humanoid 烘焙" : "正在生成动画预览");
            AnimationPreviewStatus status;
            try
            {
                // Humanoid/Muscle 动画必须让 Unity Editor 采样成目标骨架 TRS。
                // 快速预览只能合并已解出的普通 Transform 曲线，不能当成身体动作验收。
                status = needsUnityBake
                    ? await _previewCache.EnsureUnityBakeAsync(
                        model,
                        animation,
                        CancellationToken.None,
                        message => BeginInvoke(() => UpdateStatus(message)))
                    : await _previewCache.EnsureAsync(
                        model,
                        animation,
                        CancellationToken.None,
                        message => BeginInvoke(() => UpdateStatus(message)));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, needsUnityBake ? "Unity 烘焙失败" : "动画预览失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                RebuildAnimationList();
                return;
            }

            RebuildAnimationList();
            if (needsUnityBake)
            {
                ReloadBakeCacheSummary();
            }
            RefreshCurrentModelDetailText();
            UpdateStatus($"{(needsUnityBake ? "Unity 烘焙" : "动画预览")}: {status.Status}");
            if (!string.Equals(status.Status, "可播放", StringComparison.OrdinalIgnoreCase))
            {
                if (OpenUnityBakeDiagnosticIfRequested(needsUnityBake, openAfterGenerate, status))
                {
                    return;
                }

                var title = needsUnityBake
                    ? "Unity 烘焙: " + (string.IsNullOrWhiteSpace(status.Status) ? "未可播放" : status.Status)
                    : "动画预览失败";
                MessageBox.Show(
                    this,
                    status.Message ?? (needsUnityBake ? "Unity 烘焙失败。" : "生成动画预览失败。"),
                    title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (openAfterGenerate && !string.IsNullOrWhiteSpace(status.GltfPath))
            {
                OpenPathWithF3d(status.GltfPath);
            }
        }

        private async Task BakeSelectedModelAnimationsAsync()
        {
            var model = _detailModel ?? SelectedItems().FirstOrDefault();
            if (model == null || _previewCache == null)
            {
                return;
            }

            var animations = SelectedAnimationCandidates()
                .Where(x => !x.IsUnreal && RequiresUnityBake(x))
                .ToList();
            if (animations.Count == 0)
            {
                MessageBox.Show(this, "请先在当前模型动画列表里选择需要 Unity 烘焙的动画。", "批量烘焙", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            await BakeModelAnimationsAsync(model, animations, "Unity 批量烘焙");
        }

        private async Task BakeVisibleModelAnimationsAsync()
        {
            var model = _detailModel ?? SelectedItems().FirstOrDefault();
            if (model == null || _previewCache == null)
            {
                return;
            }

            var animations = _visibleDetailAnimations
                .Where(x => !x.IsUnreal && RequiresUnityBake(x))
                .ToList();
            if (animations.Count == 0)
            {
                MessageBox.Show(this, "当前可见动画里没有需要 Unity 烘焙的动画。可以先用过滤框缩小范围。", "批量烘焙", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var pendingCount = CountPendingUnityBakeAnimations(model, animations);
            var confirm = MessageBox.Show(
                this,
                $"当前可见动画里有 {animations.Count} 个 Unity Humanoid/Muscle 动画，其中 {pendingCount} 个仍需烘焙。这个过程可能会持续较久。是否继续？",
                "批量烘焙当前可见动画",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            await BakeModelAnimationsAsync(model, animations, "Unity 可见动画批量烘焙");
        }

        private async Task BakeModelAnimationsAsync(LibraryModelItem model, List<LibraryAnimationCandidate> animations, string label)
        {
            var requestedCount = animations.Count;
            var pendingAnimations = animations
                .Where(x => !IsUnityBakeAlreadyProcessedTerminal(model, x))
                .ToList();
            var skippedProcessed = requestedCount - pendingAnimations.Count;
            var readyAnimations = pendingAnimations
                .Where(HasProductionBakeOracle)
                .ToList();
            var skippedMissingAvatar = pendingAnimations.Count - readyAnimations.Count;
            var report = new UnityBakeBatchUiReport(_root, label, "modelAnimations", requestedCount, readyAnimations.Count, skippedProcessed)
            {
                SkippedMissingAvatarOracle = skippedMissingAvatar
            };
            AddSkippedMissingAvatarItems(report, model, pendingAnimations.Except(readyAnimations).ToList());
            if (readyAnimations.Count == 0)
            {
                RebuildAnimationList();
                RefreshCurrentModelDetailText();
                var skippedReportPath = WriteUnityBakeBatchUiReport(report);
                UpdateStatus($"{label}: 已跳过 {skippedProcessed} 个已处理终态动画，{skippedMissingAvatar} 个缺 Avatar oracle | 报告 {skippedReportPath}");
                MessageBox.Show(
                    this,
                    skippedMissingAvatar > 0
                        ? "选中的 Unity 动画缺少可信生产 Avatar oracle，已跳过。请先恢复/导入原始 Unity Avatar asset 或重建包含完整 HumanDescription 的索引。"
                        : "选中的 Unity 动画都已有可信 baked glTF、静态姿态或需人工验收结果，不需要默认重复烘焙。",
                    label,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var failures = new List<string>();
            var successCount = 0;
            for (var i = 0; i < readyAnimations.Count; i++)
            {
                var animation = readyAnimations[i];
                var item = report.StartItem(model, animation, report.Items.Count + 1);
                var inputError = ValidateAnimationPreviewInputs(model, animation);
                if (!string.IsNullOrWhiteSpace(inputError))
                {
                    failures.Add($"{animation.Name}: {inputError}");
                    report.FinishItem(item, "failed", inputError, null);
                    continue;
                }

                UpdateStatus($"{label} {i + 1}/{readyAnimations.Count}: 成功 {successCount}，失败 {failures.Count}，已处理 {skippedProcessed}，缺Avatar {skippedMissingAvatar} | {animation.Name}");
                try
                {
                    var status = await _previewCache.EnsureUnityBakeAsync(
                        model,
                        animation,
                        CancellationToken.None,
                        message => BeginInvoke(() => UpdateStatus(message)));
                    if (!string.Equals(status.Status, "可播放", StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"{animation.Name}: {status.Message ?? status.Status}");
                        report.FinishItem(item, "failed", status.Message ?? status.Status, status);
                    }
                    else
                    {
                        successCount++;
                        report.FinishItem(item, "baked", null, status);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{animation.Name}: {ex.Message}");
                    report.FinishItem(item, "failed", ex.Message, null);
                }

                RebuildAnimationList();
                ReloadBakeCacheSummary();
                RefreshCurrentModelDetailText();
            }

            report.Complete(successCount, failures.Count);
            var reportPath = WriteUnityBakeBatchUiReport(report);
            RebuildAnimationList();
            ReloadBakeCacheSummary();
            RefreshCurrentModelDetailText();
            if (failures.Count == 0)
            {
                UpdateStatus($"{label}完成: 成功 {successCount}/{readyAnimations.Count}，已处理 {skippedProcessed}，缺Avatar {skippedMissingAvatar} | 报告 {reportPath}");
                return;
            }

            UpdateStatus($"{label}完成: 成功 {successCount}/{readyAnimations.Count}，失败 {failures.Count}，已处理 {skippedProcessed}，缺Avatar {skippedMissingAvatar} | 报告 {reportPath}");
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine + Environment.NewLine, failures.Take(12))
                    + Environment.NewLine
                    + Environment.NewLine
                    + $"完整批次报告: {reportPath}"
                    + Environment.NewLine
                    + "要重试失败项，可以按报告里的动画名过滤当前模型动画列表，再执行批量烘焙当前可见项。缺 Avatar oracle 的跳过项需要先恢复/导入原始 Unity Avatar asset。",
                "批量烘焙未全部完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private int CountPendingUnityBakeAnimations(LibraryModelItem model, IEnumerable<LibraryAnimationCandidate> animations)
        {
            return animations.Count(x => !IsUnityBakeAlreadyProcessedTerminal(model, x));
        }

        private bool IsUnityBakeAlreadyPlayable(LibraryModelItem model, LibraryAnimationCandidate animation)
        {
            var status = _previewCache?.GetStatus(model, animation);
            return string.Equals(status?.Status, "可播放", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsUnityBakeAlreadyProcessedTerminal(LibraryModelItem model, LibraryAnimationCandidate animation)
        {
            var status = _previewCache?.GetStatus(model, animation);
            return string.Equals(status?.Status, "可播放", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status?.Status, "静态姿态", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status?.Status, "需人工验收", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasProductionBakeOracle(LibraryAnimationCandidate animation)
        {
            if (animation == null)
            {
                return false;
            }

            if (!RequiresUnityBake(animation))
            {
                return true;
            }

            return HasOriginalAvatarOracle(animation)
                || HasImportedAvatarAssetOracle(animation);
        }

        private void AddSkippedMissingAvatarItems(
            UnityBakeBatchUiReport report,
            LibraryModelItem model,
            IReadOnlyList<LibraryAnimationCandidate> animations)
        {
            if (report == null || animations == null)
            {
                return;
            }

            foreach (var animation in animations)
            {
                var item = report.StartItem(model, animation, report.Items.Count + 1);
                report.FinishItem(item, "skipped_missing_avatar_oracle", MissingAvatarOracleMessage(animation), null);
            }
        }

        private void AddSkippedMissingAvatarItems(
            UnityBakeBatchUiReport report,
            IReadOnlyList<LibraryModelItem> models,
            LibraryAnimationCandidate animation)
        {
            if (report == null || models == null)
            {
                return;
            }

            foreach (var model in models)
            {
                var modelAnimation = ResolveAnimationForModel(model, animation);
                var item = report.StartItem(model, modelAnimation, report.Items.Count + 1);
                report.FinishItem(item, "skipped_missing_avatar_oracle", MissingAvatarOracleMessage(modelAnimation), null);
            }
        }

        private static string MissingAvatarOracleMessage(LibraryAnimationCandidate animation)
        {
            var reason = string.IsNullOrWhiteSpace(animation?.ProductionUnityBakeBlockedReason)
                ? "missing_production_avatar_oracle"
                : animation.ProductionUnityBakeBlockedReason;
            return "缺少可信生产 Avatar oracle，未进入 Unity bake。需要原始 Animator.avatar、完整 HumanDescription，或导入的原始 Unity Avatar asset。原因: " + reason;
        }

        private async Task BakeSelectedLibraryAnimationModelsAsync()
        {
            var models = SelectedAnimationModels().ToList();
            if (models.Count == 0)
            {
                var fallback = SelectedAnimationModel();
                if (fallback != null)
                {
                    models.Add(fallback);
                }
            }

            await BakeLibraryAnimationModelsAsync(models, "Unity 烘焙选中模型");
        }

        private async Task BakeVisibleLibraryAnimationModelsAsync()
        {
            await BakeLibraryAnimationModelsAsync(_visibleAnimationModels.ToList(), "Unity 烘焙可见模型");
        }

        private async Task BakeLibraryAnimationModelsAsync(List<LibraryModelItem> models, string label)
        {
            var animation = _selectedLibraryAnimation?.Animation;
            if (animation == null || _previewCache == null)
            {
                return;
            }

            if (animation.IsUnreal || !RequiresUnityBake(animation))
            {
                MessageBox.Show(this, "当前动画不需要 Unity Humanoid/Muscle 烘焙。", label, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            models = models
                .Where(x => x != null)
                .GroupBy(x => NormalizePathForCompare(x.OutputPath), StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
            if (models.Count == 0)
            {
                MessageBox.Show(this, "右侧没有可烘焙模型。", label, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var pendingModels = models
                .Where(x => !IsUnityBakeAlreadyProcessedTerminal(x, ResolveAnimationForModel(x, animation)))
                .ToList();
            var skippedProcessed = models.Count - pendingModels.Count;
            var readyModels = pendingModels
                .Where(x => HasProductionBakeOracle(ResolveAnimationForModel(x, animation)))
                .ToList();
            var skippedMissingAvatar = pendingModels.Count - readyModels.Count;
            var report = new UnityBakeBatchUiReport(_root, label, "animationModels", models.Count, readyModels.Count, skippedProcessed)
            {
                SkippedMissingAvatarOracle = skippedMissingAvatar
            };
            AddSkippedMissingAvatarItems(report, pendingModels.Except(readyModels).ToList(), animation);
            if (readyModels.Count == 0)
            {
                RebuildAnimationModelList();
                var skippedReportPath = WriteUnityBakeBatchUiReport(report);
                UpdateStatus($"{label}: 已跳过 {skippedProcessed} 个已处理终态模型，{skippedMissingAvatar} 个缺 Avatar oracle | 报告 {skippedReportPath}");
                MessageBox.Show(
                    this,
                    skippedMissingAvatar > 0
                        ? "选中的模型缺少可信生产 Avatar oracle，已跳过。请先恢复/导入原始 Unity Avatar asset 或重建包含完整 HumanDescription 的索引。"
                        : "选中的模型都已有可信 baked glTF、静态姿态或需人工验收结果，不需要默认重复烘焙。",
                    label,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var confirm = MessageBox.Show(
                this,
                $"当前动画会对 {readyModels.Count} 个模型执行 Unity bake，已跳过 {skippedProcessed} 个已处理终态模型、{skippedMissingAvatar} 个缺 Avatar oracle 模型。这个过程可能会持续较久。是否继续？",
                label,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            var failures = new List<string>();
            var successCount = 0;
            for (var i = 0; i < readyModels.Count; i++)
            {
                var model = readyModels[i];
                var modelAnimation = ResolveAnimationForModel(model, animation);
                var item = report.StartItem(model, modelAnimation, report.Items.Count + 1);
                var inputError = ValidateAnimationPreviewInputs(model, modelAnimation);
                if (!string.IsNullOrWhiteSpace(inputError))
                {
                    failures.Add($"{model.Name}: {inputError}");
                    report.FinishItem(item, "failed", inputError, null);
                    continue;
                }

                UpdateStatus($"{label} {i + 1}/{readyModels.Count}: 成功 {successCount}，失败 {failures.Count}，已处理 {skippedProcessed}，缺Avatar {skippedMissingAvatar} | {model.Name}");
                try
                {
                    var status = await _previewCache.EnsureUnityBakeAsync(
                        model,
                        modelAnimation,
                        CancellationToken.None,
                        message => BeginInvoke(() => UpdateStatus(message)));
                    if (!string.Equals(status.Status, "可播放", StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"{model.Name}: {status.Message ?? status.Status}");
                        report.FinishItem(item, "failed", status.Message ?? status.Status, status);
                    }
                    else
                    {
                        successCount++;
                        report.FinishItem(item, "baked", null, status);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{model.Name}: {ex.Message}");
                    report.FinishItem(item, "failed", ex.Message, null);
                }

                RebuildAnimationModelList();
                ReloadBakeCacheSummary();
            }

            report.Complete(successCount, failures.Count);
            var reportPath = WriteUnityBakeBatchUiReport(report);
            RebuildAnimationModelList();
            ReloadBakeCacheSummary();
            if (failures.Count == 0)
            {
                UpdateStatus($"{label}完成: 成功 {successCount}/{readyModels.Count}，已处理 {skippedProcessed}，缺Avatar {skippedMissingAvatar} | 报告 {reportPath}");
                return;
            }

            UpdateStatus($"{label}完成: 成功 {successCount}/{readyModels.Count}，失败 {failures.Count}，已处理 {skippedProcessed}，缺Avatar {skippedMissingAvatar} | 报告 {reportPath}");
            MessageBox.Show(
                this,
                string.Join(Environment.NewLine + Environment.NewLine, failures.Take(12))
                    + Environment.NewLine
                    + Environment.NewLine
                    + $"完整批次报告: {reportPath}"
                    + Environment.NewLine
                    + "要重试失败项，可以先用“失败/需复查”过滤右侧模型列表，再执行烘焙可见模型。缺 Avatar oracle 的跳过项需要先恢复/导入原始 Unity Avatar asset。",
                "动画反查批量烘焙未全部完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private string WriteUnityBakeBatchUiReport(UnityBakeBatchUiReport report)
        {
            report ??= new UnityBakeBatchUiReport(_root, "Unity 批量烘焙", "unknown", 0, 0, 0);
            var directory = Path.Combine(_root ?? "", ".as_browser_cache", "unity_bake_batch_reports");
            Directory.CreateDirectory(directory);
            var fileName = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + SafeReportFileToken(report.Label) + ".json";
            var path = Path.Combine(directory, fileName);
            var json = JsonSerializer.Serialize(
                report,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            File.WriteAllText(path, json, Encoding.UTF8);
            return path;
        }

        private static string SafeReportFileToken(string value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "unity_bake_batch" : value.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                text = text.Replace(c, '_');
            }

            text = Regex.Replace(text, @"\s+", "_");
            return text.Length > 80 ? text[..80] : text;
        }

        private sealed class UnityBakeBatchUiReport
        {
            public UnityBakeBatchUiReport(
                string libraryRoot,
                string label,
                string scope,
                int requestedCount,
                int pendingCount,
                int skippedAlreadyBaked)
            {
                LibraryRoot = libraryRoot ?? "";
                Label = label ?? "";
                Scope = scope ?? "";
                RequestedCount = requestedCount;
                PendingCount = pendingCount;
                SkippedAlreadyBaked = skippedAlreadyBaked;
                StartedAtUtc = DateTime.UtcNow;
            }

            public string LibraryRoot { get; set; }
            public string Label { get; set; }
            public string Scope { get; set; }
            public DateTime StartedAtUtc { get; set; }
            public DateTime? CompletedAtUtc { get; set; }
            public int RequestedCount { get; set; }
            public int PendingCount { get; set; }
            public int SkippedAlreadyBaked { get; set; }
            public int SkippedMissingAvatarOracle { get; set; }
            public int SuccessCount { get; set; }
            public int FailureCount { get; set; }
            public Dictionary<string, int> AvatarSourceCounts => CountItemsByString(x => x.AvatarSource);
            public Dictionary<string, int> AvatarAssetCounts => CountItemsByString(x => x.AvatarAsset);
            public Dictionary<string, int> AvatarMatchKeyCounts => CountItemsByString(x => x.AvatarMatchKey);
            public List<UnityBakeBatchUiReportItem> Items { get; } = new();

            public UnityBakeBatchUiReportItem StartItem(
                LibraryModelItem model,
                LibraryAnimationCandidate animation,
                int index)
            {
                var item = new UnityBakeBatchUiReportItem
                {
                    Index = index,
                    ModelName = model?.Name ?? "",
                    ModelPath = model?.OutputPath ?? "",
                    AnimationName = animation?.Name ?? "",
                    AnimationPath = animation?.BestPath ?? "",
                    RelationSource = animation?.RelationSource ?? "",
                    AvatarSource = animation?.ProductionUnityBakeAvatarSource ?? "",
                    AvatarAsset = animation?.ProductionUnityBakeAvatarAsset ?? "",
                    AvatarMatchKey = animation?.ProductionUnityBakeAvatarMatchKey ?? "",
                    StartedAtUtc = DateTime.UtcNow,
                    Status = "running"
                };
                Items.Add(item);
                return item;
            }

            public void FinishItem(
                UnityBakeBatchUiReportItem item,
                string status,
                string message,
                AnimationPreviewStatus previewStatus)
            {
                if (item == null)
                {
                    return;
                }

                item.CompletedAtUtc = DateTime.UtcNow;
                item.DurationSeconds = Math.Round((item.CompletedAtUtc.Value - item.StartedAtUtc).TotalSeconds, 3);
                item.Status = status ?? "";
                item.Message = message ?? previewStatus?.Message ?? "";
                item.PreviewStatus = previewStatus?.Status ?? "";
                item.BakedGltfPath = previewStatus?.GltfPath ?? "";
                item.ReportPath = previewStatus?.ValidationPath ?? "";
            }

            public void Complete(int successCount, int failureCount)
            {
                CompletedAtUtc = DateTime.UtcNow;
                SuccessCount = successCount;
                FailureCount = failureCount;
            }

            private Dictionary<string, int> CountItemsByString(Func<UnityBakeBatchUiReportItem, string> selector)
            {
                var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in Items)
                {
                    var key = selector(item);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    key = key.Trim();
                    result[key] = result.TryGetValue(key, out var oldCount) ? oldCount + 1 : 1;
                }

                return result;
            }
        }

        private sealed class UnityBakeBatchUiReportItem
        {
            public int Index { get; set; }
            public string ModelName { get; set; } = "";
            public string ModelPath { get; set; } = "";
            public string AnimationName { get; set; } = "";
            public string AnimationPath { get; set; } = "";
            public string RelationSource { get; set; } = "";
            public string AvatarSource { get; set; } = "";
            public string AvatarAsset { get; set; } = "";
            public string AvatarMatchKey { get; set; } = "";
            public string Status { get; set; } = "";
            public string PreviewStatus { get; set; } = "";
            public string Message { get; set; } = "";
            public string BakedGltfPath { get; set; } = "";
            public string ReportPath { get; set; } = "";
            public DateTime StartedAtUtc { get; set; }
            public DateTime? CompletedAtUtc { get; set; }
            public double DurationSeconds { get; set; }
        }

        private async Task GenerateSelectedLibraryAnimationPreviewAsync(bool openAfterGenerate)
        {
            var animation = _selectedLibraryAnimation?.Animation;
            var model = SelectedAnimationModel();
            if (model == null || animation == null || _previewCache == null)
            {
                return;
            }

            animation = ResolveAnimationForModel(model, animation);
            if (animation.IsUnreal)
            {
                await GenerateUnrealAnimationPreviewAsync(model, animation, openAfterGenerate, RebuildAnimationModelList);
                return;
            }

            var inputError = ValidateAnimationPreviewInputs(model, animation);
            if (!string.IsNullOrWhiteSpace(inputError))
            {
                MessageBox.Show(this, inputError, "动画预览", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var needsUnityBake = RequiresUnityBake(animation);
            UpdateStatus(needsUnityBake ? "正在执行 Unity Humanoid 烘焙" : "正在生成动画预览");
            AnimationPreviewStatus status;
            try
            {
                status = needsUnityBake
                    ? await _previewCache.EnsureUnityBakeAsync(
                        model,
                        animation,
                        CancellationToken.None,
                        message => BeginInvoke(() => UpdateStatus(message)))
                    : await _previewCache.EnsureAsync(
                        model,
                        animation,
                        CancellationToken.None,
                        message => BeginInvoke(() => UpdateStatus(message)));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, needsUnityBake ? "Unity 烘焙失败" : "动画预览失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                RebuildAnimationModelList();
                return;
            }

            RebuildAnimationModelList();
            if (needsUnityBake)
            {
                ReloadBakeCacheSummary();
            }
            RefreshCurrentModelDetailText();
            UpdateStatus($"{(needsUnityBake ? "Unity 烘焙" : "动画预览")}: {status.Status}");
            if (!string.Equals(status.Status, "可播放", StringComparison.OrdinalIgnoreCase))
            {
                if (OpenUnityBakeDiagnosticIfRequested(needsUnityBake, openAfterGenerate, status))
                {
                    return;
                }

                var title = needsUnityBake
                    ? "Unity 烘焙: " + (string.IsNullOrWhiteSpace(status.Status) ? "未可播放" : status.Status)
                    : "动画预览失败";
                MessageBox.Show(
                    this,
                    status.Message ?? (needsUnityBake ? "Unity 烘焙失败。" : "生成动画预览失败。"),
                    title,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (openAfterGenerate && !string.IsNullOrWhiteSpace(status.GltfPath))
            {
                OpenPathWithF3d(status.GltfPath);
            }
        }

        private async Task GenerateUnrealAnimationPreviewAsync(
            LibraryModelItem model,
            LibraryAnimationCandidate animation,
            bool openAfterGenerate,
            Action rebuild)
        {
            var inputError = ValidateUnrealAnimationPreviewInputs(model, animation);
            if (!string.IsNullOrWhiteSpace(inputError))
            {
                MessageBox.Show(this, inputError, "UE 动画预览", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            UpdateSelectedAnimationStatus("UE预览生成中");
            UpdateStatus("正在生成 UE 动画预览");
            AnimationPreviewStatus status;
            try
            {
                status = await _previewCache.EnsureUnrealAsync(
                    model,
                    animation,
                    CancellationToken.None,
                    message => BeginInvoke(() => UpdateStatus(message)));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "UE 动画预览失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                rebuild();
                return;
            }

            rebuild();
            UpdateStatus($"UE 动画预览: {status.Status}");
            if (!string.Equals(status.Status, "可播放", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    this,
                    status.Message ?? "生成 UE 动画预览失败。",
                    "UE 动画预览失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (openAfterGenerate && !string.IsNullOrWhiteSpace(status.GltfPath))
            {
                OpenPathWithF3d(status.GltfPath);
            }
        }

        private static bool OpenUnityBakeDiagnosticIfRequested(bool needsUnityBake, bool openAfterGenerate, AnimationPreviewStatus status)
        {
            if (!needsUnityBake
                || !openAfterGenerate
                || status == null
                || string.IsNullOrWhiteSpace(status.GltfPath)
                || !File.Exists(status.GltfPath)
                || !IsOpenableUnityBakeDiagnosticStatus(status.Status))
            {
                return false;
            }

            OpenPathWithF3d(status.GltfPath);
            return true;
        }

        private static bool IsOpenableUnityBakeDiagnosticStatus(string status)
        {
            return string.Equals(status, "静态姿态", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "需人工验收", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "已烘焙但需重建", StringComparison.OrdinalIgnoreCase);
        }

        private static string ValidateUnrealAnimationPreviewInputs(LibraryModelItem model, LibraryAnimationCandidate animation)
        {
            if (model == null || animation == null)
            {
                return "没有选中模型或动画。";
            }

            if (string.IsNullOrWhiteSpace(model.OutputPath) || !File.Exists(model.OutputPath))
            {
                return "模型 GLB 文件不存在，无法生成 UE 动画预览。"
                    + Environment.NewLine
                    + $"模型文件: {model.OutputPath}";
            }

            if (string.IsNullOrWhiteSpace(animation.BestPath) || !File.Exists(animation.BestPath))
            {
                return "UE 动画文件不存在，无法生成预览。"
                    + Environment.NewLine
                    + $"动画文件: {animation.BestPath}";
            }

            if (!animation.BestPath.EndsWith(".ueanim", StringComparison.OrdinalIgnoreCase))
            {
                return "当前 UE 动画不是 .ueanim 轨道文件，暂时只能作为关系元数据查看。"
                    + Environment.NewLine
                    + $"动画文件: {animation.BestPath}";
            }

            if (!animation.IsUsableCandidate)
            {
                return "当前 UE 动画不是可用候选，不能生成默认预览。"
                    + Environment.NewLine
                    + $"验证: {FormatAnimationValidation(animation).Trim()}";
            }

            return null;
        }

        private static string ValidateAnimationPreviewInputs(LibraryModelItem model, LibraryAnimationCandidate animation)
        {
            if (model == null || animation == null)
            {
                return "没有选中模型或动画。";
            }

            if (!animation.IsExplicit)
            {
                return "当前动画关系不是 Unity 显式关系，不能进入默认 glTF 预览或 Unity 烘焙。"
                    + Environment.NewLine
                    + "骨骼路径、结构兼容、名称或人工定向匹配只能用于诊断，不能当作素材库默认绑定。"
                    + Environment.NewLine
                    + $"关系来源: {EmptyAsUnknown(animation.RelationSource)}";
            }

            if (string.IsNullOrWhiteSpace(model.OutputPath) || !File.Exists(model.OutputPath))
            {
                return "模型 glTF 文件不存在，无法生成动画预览。"
                    + Environment.NewLine
                    + $"模型文件: {model.OutputPath}"
                    + Environment.NewLine
                    + $"Unity 来源: {model.Source}";
            }

            if (string.IsNullOrWhiteSpace(animation.BestPath) || !File.Exists(animation.BestPath))
            {
                return "动画导出文件不存在，无法生成动画预览。"
                    + Environment.NewLine
                    + $"动画文件: {animation.BestPath}"
                    + Environment.NewLine
                    + $"Unity 来源: {animation.Source}";
            }

            if (IsBlendShapeOnlyAnimation(animation))
            {
                var blendShapeError = ValidateBlendShapeAnimationPreview(model, animation);
                if (!string.IsNullOrWhiteSpace(blendShapeError))
                {
                    return blendShapeError;
                }
            }

            return null;
        }

        private static string ValidateBlendShapeAnimationPreview(LibraryModelItem model, LibraryAnimationCandidate animation)
        {
            var morphTargets = ReadGltfMorphTargetNames(model.OutputPath);
            if (morphTargets.Count == 0)
            {
                return "该动画是 BlendShape/表情动画，但当前模型 glTF 没有 morph target，不能可靠绑定预览。"
                    + Environment.NewLine
                    + $"模型: {model.Name}"
                    + Environment.NewLine
                    + $"动画: {animation.Name}"
                    + Environment.NewLine
                    + "请选择带表情 morph target 的同角色模型，或换用 Transform/Humanoid 身体动画。";
            }

            if (animation.IsExplicit)
            {
                return null;
            }

            var modelTokens = BuildIdentityTokens(model.Name, BuildLocalIdentityText(model.OutputPath), BuildLocalIdentityText(model.Source));
            var animationTokens = BuildIdentityTokens(animation.Name, BuildLocalIdentityText(animation.OutputPath), BuildLocalIdentityText(animation.AnimationAssetPath));
            if (modelTokens.Overlaps(animationTokens))
            {
                return null;
            }

            var animationBlendShapes = ReadAnimationBlendShapeNames(animation.AnimationAssetPath);
            if (animationBlendShapes.Count > 0 && morphTargets.Overlaps(animationBlendShapes))
            {
                return null;
            }

            return "该动画是 BlendShape/表情动画，但和当前模型没有可靠的 Unity 显式关系、角色命名交集或 morph target 交集，不能可靠绑定预览。"
                + Environment.NewLine
                + $"模型: {model.Name}"
                + Environment.NewLine
                + $"动画: {animation.Name}"
                + Environment.NewLine
                + "这类动画通常是某个角色脸部/表情专用动画，例如 Ann_Angry 不应直接套到 ZFlowerModel。";
        }

        private static bool IsBlendShapeOnlyAnimation(LibraryAnimationCandidate animation)
        {
            if (animation == null)
            {
                return false;
            }

            var type = animation.AnimationType ?? "";
            return type.IndexOf("BlendShape", StringComparison.OrdinalIgnoreCase) >= 0
                && !type.Equals("TransformBodyAnimation", StringComparison.OrdinalIgnoreCase)
                && (animation.BindingPaths == null || animation.BindingPaths.Length == 0);
        }

        private static bool GltfHasMorphTargets(string gltfPath)
        {
            return ReadGltfMorphTargetNames(gltfPath).Count > 0;
        }

        private static HashSet<string> ReadGltfMorphTargetNames(string gltfPath)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(gltfPath) || !File.Exists(gltfPath))
            {
                return result;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(gltfPath));
                if (document.RootElement.TryGetProperty("meshes", out var meshes) && meshes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mesh in meshes.EnumerateArray())
                    {
                        if (mesh.TryGetProperty("extras", out var extras)
                            && extras.TryGetProperty("targetNames", out var targetNames)
                            && targetNames.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var targetName in targetNames.EnumerateArray())
                            {
                                var name = targetName.GetString();
                                if (!string.IsNullOrWhiteSpace(name))
                                {
                                    result.Add(NormalizeBlendShapeName(name));
                                }
                            }
                        }

                        if (mesh.TryGetProperty("primitives", out var primitives) && primitives.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var primitive in primitives.EnumerateArray())
                            {
                                if (primitive.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
                                {
                                    result.Add("__has_targets");
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return result;
        }

        private static HashSet<string> ReadAnimationBlendShapeNames(string animationAssetPath)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(animationAssetPath) || !File.Exists(animationAssetPath))
            {
                return result;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(animationAssetPath));
                ReadAnimationBlendShapeNames(document.RootElement, result);
            }
            catch
            {
            }

            return result;
        }

        private static void ReadAnimationBlendShapeNames(JsonElement element, HashSet<string> result)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    string customTypeName = null;
                    string attributeName = null;
                    string attribute = null;
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.NameEquals("customTypeName"))
                        {
                            customTypeName = property.Value.GetString();
                        }
                        else if (property.NameEquals("attributeName"))
                        {
                            attributeName = property.Value.GetString();
                        }
                        else if (property.NameEquals("attribute"))
                        {
                            attribute = property.Value.GetString();
                        }

                        ReadAnimationBlendShapeNames(property.Value, result);
                    }

                    if (string.Equals(customTypeName, "BlendShape", StringComparison.OrdinalIgnoreCase))
                    {
                        AddBlendShapeName(result, attributeName);
                    }

                    AddBlendShapeName(result, attribute);
                    break;

                case JsonValueKind.Array:
                    foreach (var child in element.EnumerateArray())
                    {
                        ReadAnimationBlendShapeNames(child, result);
                    }

                    break;
            }
        }

        private static void AddBlendShapeName(HashSet<string> result, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var name = value;
            if (name.StartsWith("blendShape.", StringComparison.OrdinalIgnoreCase))
            {
                name = name["blendShape.".Length..];
            }
            else if (name.StartsWith("blendShape_", StringComparison.OrdinalIgnoreCase))
            {
                name = name["blendShape_".Length..];
            }

            name = NormalizeBlendShapeName(name);
            if (!string.IsNullOrWhiteSpace(name))
            {
                result.Add(name);
            }
        }

        private static HashSet<string> BuildIdentityTokens(params string[] values)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values)
            {
                foreach (var token in SplitIdentityTokens(value))
                {
                    result.Add(token);
                }
            }

            return result;
        }

        private static string BuildLocalIdentityText(string pathOrName)
        {
            if (string.IsNullOrWhiteSpace(pathOrName))
            {
                return "";
            }

            try
            {
                var parts = new List<string>();
                var fileName = Path.GetFileNameWithoutExtension(pathOrName);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    parts.Add(fileName);
                }

                var directory = Path.GetDirectoryName(pathOrName);
                for (var i = 0; i < 2 && !string.IsNullOrWhiteSpace(directory); i++)
                {
                    var name = Path.GetFileName(directory);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        parts.Add(name);
                    }

                    directory = Path.GetDirectoryName(directory);
                }

                return string.Join(" ", parts);
            }
            catch
            {
                return pathOrName;
            }
        }

        private static IEnumerable<string> SplitIdentityTokens(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            var text = Regex.Replace(value, "([a-z])([A-Z])", "$1_$2");
            foreach (var raw in Regex.Split(text, @"[^A-Za-z0-9]+"))
            {
                var token = raw.Trim().ToLowerInvariant();
                if (token.Length < 3 || IsGenericIdentityToken(token))
                {
                    continue;
                }

                yield return token;
                if (token.EndsWith("model", StringComparison.OrdinalIgnoreCase) && token.Length > 8)
                {
                    yield return token[..^5];
                }
            }
        }

        private static bool IsGenericIdentityToken(string token)
        {
            return token is "assets" or "asset" or "models" or "model" or "mesh" or "prefab"
                or "animations" or "animation" or "anim" or "character" or "characters"
                or "ingame" or "outgame" or "idle" or "angry" or "face" or "body"
                or "data" or "source" or "sharedassets";
        }

        private static string NormalizeBlendShapeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            return Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "");
        }

        private LibraryAnimationCandidate SelectedAnimationCandidate()
        {
            if (_animationList.SelectedItems.Count > 0)
            {
                return _animationList.SelectedItems[0].Tag as LibraryAnimationCandidate;
            }

            return _detailAnimations.FirstOrDefault();
        }

        private IEnumerable<LibraryAnimationCandidate> SelectedAnimationCandidates()
        {
            foreach (ListViewItem item in _animationList.SelectedItems)
            {
                if (item.Tag is LibraryAnimationCandidate animation)
                {
                    yield return animation;
                }
            }
        }

        private void AnimationList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.C)
            {
                CopySelectedAnimationBestPaths();
                e.SuppressKeyPress = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedAnimationNames();
                e.SuppressKeyPress = true;
            }
        }

        private IEnumerable<LibraryAnimationUsage> SelectedLibraryAnimations()
        {
            foreach (ListViewItem item in _libraryAnimationList.SelectedItems)
            {
                if (item.Tag is LibraryAnimationUsage usage)
                {
                    yield return usage;
                }
            }
        }

        private void LibraryAnimationList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.C)
            {
                CopySelectedLibraryAnimationBestPaths();
                e.SuppressKeyPress = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedLibraryAnimationNames();
                e.SuppressKeyPress = true;
            }
        }

        private void UpdateSelectedAnimationStatus(string status)
        {
            if (_animationList.SelectedItems.Count > 0)
            {
                _animationList.SelectedItems[0].Text = status;
            }
        }

        private static void OpenPathWithF3d(string path)
        {
            path = PreparePathForF3d(path);
            var f3d = FindF3dForOpen();
            var startInfo = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(f3d) ? path : f3d,
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(f3d))
            {
                startInfo.ArgumentList.Add(path);
            }

            Process.Start(startInfo);
        }

        private static string PreparePathForF3d(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length < 240 || !File.Exists(path))
            {
                return path;
            }

            try
            {
                var sourceDirectory = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
                {
                    return path;
                }

                var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..20];
                var targetDirectory = Path.Combine(Path.GetTempPath(), "AnimeStudioF3DOpenCache", hash);
                MirrorDirectoryForF3d(sourceDirectory, targetDirectory);
                var mirroredPath = Path.Combine(targetDirectory, Path.GetFileName(path));
                return File.Exists(mirroredPath) ? mirroredPath : path;
            }
            catch
            {
                return path;
            }
        }

        private static void MirrorDirectoryForF3d(string sourceDirectory, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);
            foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, directory)));
            }

            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? targetDirectory);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static string FindF3dForOpen()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidate = Path.Combine(programFiles, "F3D", "bin", "f3d.exe");
            return File.Exists(candidate) ? candidate : null;
        }

        private void OpenSelectedFolder()
        {
            var item = SelectedItems().FirstOrDefault();
            if (item == null)
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = Directory.Exists(item.OutputPath) ? $"\"{item.OutputPath}\"" : $"/select,\"{item.OutputPath}\"",
                UseShellExecute = true
            });
        }

        private void OpenSelectedTexture()
        {
            var item = SelectedTextureItems().FirstOrDefault();
            if (item == null || string.IsNullOrWhiteSpace(item.OutputPath))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = item.OutputPath,
                UseShellExecute = true
            });
        }

        private void OpenSelectedVfxFolder()
        {
            var item = SelectedVfxItems().FirstOrDefault();
            if (item == null)
            {
                return;
            }

            var path = Directory.Exists(item.OutputPath)
                ? item.OutputPath
                : Path.GetDirectoryName(item.OutputPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }

        private void SetSelectedIgnored(bool ignored)
        {
            if (_curationStore == null)
            {
                return;
            }

            foreach (var item in SelectedItems())
            {
                _curationStore.SetIgnored(item, ignored);
            }

            ApplyFilter();
            UpdateDetails();
        }

        private void SetSelectedModelsFavorite(bool favorite)
        {
            if (_curationStore == null)
            {
                return;
            }

            foreach (var item in SelectedItems())
            {
                _curationStore.SetFavoriteModel(item, favorite);
            }

            ApplyFilter();
            UpdateDetails();
        }

        private void SetSelectedModelAnimationsFavorite(bool favorite)
        {
            if (_curationStore == null)
            {
                return;
            }

            foreach (var animation in SelectedAnimationCandidates())
            {
                _curationStore.SetFavoriteAnimation(animation, favorite);
            }

            RebuildAnimationList();
            RebuildLibraryAnimationList();
        }

        private void SetSelectedLibraryAnimationsFavorite(bool favorite)
        {
            if (_curationStore == null)
            {
                return;
            }

            foreach (var usage in SelectedLibraryAnimations())
            {
                _curationStore.SetFavoriteAnimation(usage, favorite);
            }

            RebuildAnimationList();
            RebuildLibraryAnimationList();
        }

        private void CopySelectedPath()
        {
            var item = SelectedItems().FirstOrDefault();
            if (item != null)
            {
                Clipboard.SetText(item.OutputPath);
            }
        }

        private void CopySelectedAnimationPaths()
        {
            var item = SelectedItems().FirstOrDefault();
            if (item == null)
            {
                return;
            }

            var paths = _animationIndex.FindForModel(item)
                .Select(x => x.BestPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length > 0)
            {
                Clipboard.SetText(string.Join(Environment.NewLine, paths));
            }
        }

        private void CopySelectedAnimationNames()
        {
            var names = SelectedAnimationCandidates()
                .Select(x => x.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length > 0)
            {
                Clipboard.SetText(string.Join(Environment.NewLine, names));
            }
        }

        private void CopySelectedAnimationBestPaths()
        {
            var paths = SelectedAnimationCandidates()
                .Select(x => x.BestPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length > 0)
            {
                Clipboard.SetText(string.Join(Environment.NewLine, paths));
            }
        }

        private void CopySelectedLibraryAnimationNames()
        {
            var names = SelectedLibraryAnimations()
                .Select(x => x.Animation.Name)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length > 0)
            {
                Clipboard.SetText(string.Join(Environment.NewLine, names));
            }
        }

        private void CopySelectedLibraryAnimationBestPaths()
        {
            var paths = SelectedLibraryAnimations()
                .Select(x => x.Animation.BestPath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length > 0)
            {
                Clipboard.SetText(string.Join(Environment.NewLine, paths));
            }
        }

        private void OpenSelectedAnimationFolder()
        {
            var path = SelectedAnimationCandidates()
                .Select(x => x.BestPath)
                .FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }

        private void OpenFirstAnimationFolder()
        {
            var item = SelectedItems().FirstOrDefault();
            if (item == null)
            {
                return;
            }

            var path = _animationIndex.FindForModel(item)
                .Select(x => x.BestPath)
                .FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }

        private static Image CreatePlaceholderImage(string text)
        {
            var bitmap = new Bitmap(144, 144);
            using var g = Graphics.FromImage(bitmap);
            using var back = new SolidBrush(Color.FromArgb(47, 52, 56));
            using var border = new Pen(Color.FromArgb(85, 92, 99));
            using var brush = new SolidBrush(Color.WhiteSmoke);
            g.FillRectangle(back, 0, 0, bitmap.Width, bitmap.Height);
            g.DrawRectangle(border, 0, 0, bitmap.Width - 1, bitmap.Height - 1);
            var size = g.MeasureString(text, SystemFonts.MessageBoxFont);
            g.DrawString(text, SystemFonts.MessageBoxFont, brush, (bitmap.Width - size.Width) / 2, (bitmap.Height - size.Height) / 2);
            return bitmap;
        }
    }
}
