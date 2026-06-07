using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class MainForm : Form
    {
        private readonly TableLayoutPanel _toolbarPanel = new();
        private readonly ToolStrip _commandStrip = new();
        private readonly ToolStrip _searchStrip = new();
        private readonly ToolStrip _filterStrip = new();
        private readonly ToolStripButton _openButton = new("选择素材库");
        private readonly ToolStripDropDownButton _recentButton = new("最近");
        private readonly ToolStripButton _refreshListButton = new("刷新列表");
        private readonly ToolStripButton _reloadButton = new("重新加载");
        private readonly ToolStripTextBox _searchBox = new();
        private readonly ToolStripButton _clearSearchButton = new("清除");
        private readonly ToolStripLabel _typeLabel = new("类型");
        private readonly ToolStripComboBox _kindBox = new();
        private readonly ToolStripComboBox _thumbnailStateBox = new();
        private readonly ToolStripComboBox _concurrencyBox = new();
        private readonly ToolStripButton _hideIgnoredButton = new("隐藏忽略");
        private readonly SplitContainer _split = new();
        private readonly SplitContainer _detailSplit = new();
        private readonly ListView _modelList = new();
        private readonly ListView _animationList = new();
        private readonly ImageList _images = new();
        private readonly TextBox _detailBox = new();
        private readonly StatusStrip _statusStrip = new();
        private readonly ToolStripStatusLabel _statusLabel = new();
        private readonly ContextMenuStrip _menu = new();
        private readonly System.Windows.Forms.Timer _uiRefreshTimer = new();
        private readonly RecentLibraryStore _recentStore = new();

        private string _root;
        private List<LibraryModelItem> _allModels = new();
        private List<LibraryModelItem> _visibleModels = new();
        private ThumbnailCache _thumbnailCache;
        private LibraryCurationStore _curationStore;
        private LibraryAnimationIndex _animationIndex = LibraryAnimationIndex.Empty;
        private AnimationPreviewCache _previewCache;
        private LibraryModelItem _detailModel;
        private List<LibraryAnimationCandidate> _detailAnimations = new();
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
            _openButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _recentButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _refreshListButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _reloadButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _searchBox.AutoSize = false;
            _searchBox.Width = 360;
            _searchBox.ToolTipText = "按当前类型标签里的模型继续搜索。支持 * ? 通配符、空格多词、-排除、name:/path:/kind:/type:/source:/role:";
            _searchBox.TextBox.PlaceholderText = "搜索当前标签，例如 *player*  -debug";
            _clearSearchButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _kindBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _kindBox.Width = 160;
            _thumbnailStateBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _thumbnailStateBox.Width = 120;
            _thumbnailStateBox.Items.AddRange(new object[] { "全部缩略图", "已有缩略图", "未生成", "生成失败" });
            _thumbnailStateBox.SelectedIndex = 0;
            _concurrencyBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _concurrencyBox.Width = 64;
            _concurrencyBox.Items.AddRange(new object[] { "2", "4", "6", "8", "12", "16", "24" });
            _concurrencyBox.SelectedItem = "16";
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
                new ToolStripLabel("缩略图"),
                _thumbnailStateBox,
                new ToolStripSeparator(),
                new ToolStripLabel("并发"),
                _concurrencyBox,
                new ToolStripSeparator(),
                _hideIgnoredButton
            });
            _toolbarPanel.Controls.Add(_commandStrip, 0, 0);
            _toolbarPanel.SetColumnSpan(_commandStrip, 2);
            _toolbarPanel.Controls.Add(_searchStrip, 0, 1);
            _toolbarPanel.Controls.Add(_filterStrip, 1, 1);
            RebuildModelTypeFilter();
            RebuildRecentMenu();
            Controls.Add(_toolbarPanel);

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

            _split.Dock = DockStyle.Fill;
            _split.SplitterDistance = 1100;
            _split.Panel1.Controls.Add(_modelList);
            _detailSplit.Dock = DockStyle.Fill;
            _detailSplit.Orientation = Orientation.Horizontal;
            _detailSplit.SplitterDistance = 520;
            _detailSplit.Panel1.Controls.Add(_animationList);
            _detailSplit.Panel2.Controls.Add(_detailBox);
            _split.Panel2.Controls.Add(_detailSplit);
            Controls.Add(_split);
            _split.BringToFront();

            _statusStrip.Items.Add(_statusLabel);
            Controls.Add(_statusStrip);

            _kindBox.Items.Add("All");
            _kindBox.SelectedIndex = 0;

            _menu.Items.Add("用 f3d 打开", null, (_, _) => OpenSelectedWithF3d());
            _menu.Items.Add("打开所在目录", null, (_, _) => OpenSelectedFolder());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("复制匹配动画路径", null, (_, _) => CopySelectedAnimationPaths());
            _menu.Items.Add("打开首个动画目录", null, (_, _) => OpenFirstAnimationFolder());
            _menu.Items.Add("生成并打开动画预览", null, async (_, _) => await GenerateSelectedAnimationPreviewAsync(openAfterGenerate: true));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("加入忽略", null, (_, _) => SetSelectedIgnored(true));
            _menu.Items.Add("取消忽略", null, (_, _) => SetSelectedIgnored(false));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("复制路径", null, (_, _) => CopySelectedPath());
            _modelList.ContextMenuStrip = _menu;
        }

        private static void ConfigureToolStrip(ToolStrip strip)
        {
            strip.GripStyle = ToolStripGripStyle.Hidden;
            strip.Dock = DockStyle.Fill;
            strip.AutoSize = true;
            strip.CanOverflow = true;
            strip.Padding = new Padding(6, 2, 6, 2);
        }

        private void WireEvents()
        {
            _openButton.Click += async (_, _) => await ChooseRootAsync();
            _refreshListButton.Click += (_, _) => RefreshListOnly();
            _reloadButton.Click += async (_, _) => await ReloadAsync();
            _searchBox.TextChanged += (_, _) => ApplyFilter();
            _searchBox.KeyDown += SearchBox_KeyDown;
            _clearSearchButton.Click += (_, _) => ClearSearch();
            _kindBox.SelectedIndexChanged += (_, _) => ApplyFilter();
            _thumbnailStateBox.SelectedIndexChanged += (_, _) => ApplyFilter();
            _concurrencyBox.SelectedIndexChanged += (_, _) => RestartThumbnailQueue();
            _hideIgnoredButton.CheckedChanged += (_, _) => ApplyFilter();
            _modelList.SelectedIndexChanged += (_, _) => UpdateDetails();
            _modelList.DoubleClick += (_, _) => OpenSelectedWithF3d();
            _uiRefreshTimer.Interval = 500;
            _uiRefreshTimer.Tick += (_, _) => FlushQueuedUiRefresh();
            _uiRefreshTimer.Start();
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

        private async Task OpenLibraryAsync(string root)
        {
            _root = root;
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
            _statusLabel.Text = "正在读取 asset_catalog.jsonl ...";

            try
            {
                var root = _root;
                var models = await Task.Run(() => LibraryIndexReader.LoadModels(root));
                _allModels = models.OrderBy(x => x.ResourceKind).ThenBy(x => x.Name).ToList();
                _thumbnailCache = new ThumbnailCache(root, GetThumbnailConcurrency());
                _curationStore = new LibraryCurationStore(root);
                _animationIndex = await Task.Run(() => LibraryAnimationIndex.Load(root));
                _previewCache = new AnimationPreviewCache(root);
                _recentStore.Add(root);
                RebuildRecentMenu();
                Text = $"AnimeStudio Library Browser - {Path.GetFileName(root)}";
                ResetThumbnailProgress();
                RebuildModelTypeFilter();
                RebuildKindFilter();
                ApplyFilter();
                UpdateStatus(_thumbnailCache.HasF3d ? "缩略图后台生成中" : "没有找到 f3d.exe");
                StartThumbnailQueue(_thumbnailCts.Token);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "加载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _statusLabel.Text = "加载失败";
            }
        }

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
            foreach (var kind in _allModels.Select(x => string.IsNullOrWhiteSpace(x.ResourceKind) ? "Unknown" : x.ResourceKind).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            {
                _kindBox.Items.Add(kind);
            }

            _kindBox.SelectedItem = _kindBox.Items.Contains(selected) ? selected : "All";
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
            UpdateStatus("列表已刷新");
        }

        private void ApplyFilter()
        {
            var text = _searchBox.Text?.Trim() ?? "";
            var searchTerms = BuildSearchTerms(text);
            var kind = _kindBox.SelectedItem as string ?? "All";
            IEnumerable<LibraryModelItem> query = _allModels;

            if (!IsAllModelType(_selectedModelType))
            {
                query = query.Where(x => string.Equals(x.ModelSourceLabel, _selectedModelType, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.Equals(kind, "All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(x => string.Equals(x.ResourceKind, kind, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                query = query.Where(x => MatchesSearch(x, searchTerms));
            }

            if (_hideIgnoredButton.Checked && _curationStore != null)
            {
                query = query.Where(x => !_curationStore.IsIgnored(x));
            }

            if (_thumbnailCache != null)
            {
                query = ApplyThumbnailStateFilter(query);
            }

            _visibleModels = query.ToList();
            _modelList.VirtualListSize = _visibleModels.Count;
            PreloadVisibleThumbnails();
            _modelList.Refresh();
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

        private static bool IsAllModelType(string label)
        {
            return string.Equals(label, "全部", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "All", StringComparison.OrdinalIgnoreCase);
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

                var field = "";
                var separator = raw.IndexOf(':');
                if (separator > 0)
                {
                    var fieldName = raw[..separator].Trim().ToLowerInvariant();
                    if (IsKnownSearchField(fieldName))
                    {
                        field = fieldName;
                        raw = raw[(separator + 1)..];
                    }
                }

                raw = raw.Trim();
                if (raw.Length == 0)
                {
                    continue;
                }

                terms.Add(new SearchTerm(field, raw, exclude, CreateWildcardRegex(raw)));
            }

            return terms;
        }

        private static bool IsKnownSearchField(string field)
        {
            return field is "name" or "path" or "kind" or "source" or "role" or "file" or "type";
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

        private static bool MatchesSearch(LibraryModelItem item, List<SearchTerm> terms)
        {
            if (terms.Count == 0)
            {
                return true;
            }

            foreach (var term in terms)
            {
                var matched = SearchValues(item, term.Field).Any(value => term.IsMatch(value));
                if (term.Exclude ? matched : !matched)
                {
                    return false;
                }
            }

            return true;
        }

        private static IEnumerable<string> SearchValues(LibraryModelItem item, string field)
        {
            return field switch
            {
                "name" => Values(item.Name, item.FileName),
                "file" => Values(item.FileName),
                "path" => Values(item.OutputPath),
                "kind" => Values(item.ResourceKind),
                "source" => Values(item.Source, item.SourceType),
                "role" => Values(item.LibraryRole),
                "type" => Values(item.ModelSourceLabel, item.SourceType, item.LibraryRole),
                _ => Values(item.Name, item.FileName, item.OutputPath, item.ResourceKind, item.ModelSourceLabel, item.Source, item.SourceType, item.LibraryRole)
            };
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
            public SearchTerm(string field, string pattern, bool exclude, Regex wildcardRegex)
            {
                Field = field;
                Pattern = pattern;
                Exclude = exclude;
                WildcardRegex = wildcardRegex;
            }

            public string Field { get; }
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
            var animationCount = _animationIndex.CountExplicitForModel(item);
            e.Item = new ListViewItem(ShortLabel(item, animationCount))
            {
                ImageIndex = imageIndex,
                ToolTipText = animationCount > 0
                    ? $"{item.OutputPath}{Environment.NewLine}类型: {item.ModelSourceLabel}{Environment.NewLine}显式动画: {animationCount}"
                    : $"{item.OutputPath}{Environment.NewLine}类型: {item.ModelSourceLabel}"
            };
        }

        private void ModelList_CacheVirtualItems(object sender, CacheVirtualItemsEventArgs e)
        {
            PreloadThumbnailRange(e.StartIndex, e.EndIndex);
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

        private static string ShortLabel(LibraryModelItem item, int animationCount)
        {
            var name = string.IsNullOrWhiteSpace(item.Name) ? item.FileName : item.Name;
            var suffix = animationCount > 0 ? $" [显式 {animationCount}]" : "";
            var sourceBadge = $" [{item.ModelSourceLabel}]";
            var maxNameLength = Math.Max(8, 34 - suffix.Length - sourceBadge.Length);
            var shortName = name.Length <= maxNameLength ? name : name[..Math.Max(1, maxNameLength - 3)] + "...";
            return shortName + sourceBadge + suffix;
        }

        private void StartThumbnailQueue(CancellationToken cancellationToken)
        {
            if (_thumbnailCache?.HasPersistentRenderer != true && _thumbnailCache?.HasF3d != true)
            {
                UpdateStatus("没有可用缩略图渲染器");
                return;
            }

            var pendingItems = _allModels
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
            if (string.IsNullOrWhiteSpace(_root) || _allModels.Count == 0)
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
                _thumbnailTotal = _allModels.Count;
                _thumbnailCached = 0;
                _thumbnailFailed = 0;
                _thumbnailPending = 0;
                _thumbnailActive = 0;
                return;
            }

            _thumbnailTotal = _allModels.Count;
            _thumbnailCached = _allModels.Count(_thumbnailCache.IsCached);
            _thumbnailFailed = _allModels.Count(_thumbnailCache.IsFailed);
            _thumbnailPending = Math.Max(0, _thumbnailTotal - _thumbnailCached - _thumbnailFailed);
            _thumbnailActive = 0;
        }

        private void UpdateStatus(string message)
        {
            var pending = Math.Max(0, Volatile.Read(ref _thumbnailPending));
            var active = Math.Max(0, Volatile.Read(ref _thumbnailActive));
            var renderer = _thumbnailCache?.HasPersistentRenderer == true ? "常驻GL" : "无内置渲染";
            var fallback = _thumbnailCache?.HasF3d == true ? "f3d fallback" : "无f3d";
            _statusLabel.Text =
                $"{message} | 显示 {_visibleModels.Count}/{_allModels.Count} | " +
                $"缩略图 {Volatile.Read(ref _thumbnailCached)}/{_thumbnailTotal} | " +
                $"失败 {Volatile.Read(ref _thumbnailFailed)} | 队列 {pending} | 运行 {active} | 并发 {GetThumbnailConcurrency()}";
            _statusLabel.Text += $" | {renderer} | {fallback}";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _uiRefreshTimer.Stop();
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
            var explicitCount = animations.Count(x => x.IsExplicit);
            _detailBox.Text =
                $"名称: {item.Name}{Environment.NewLine}" +
                $"模型来源: {item.ModelSourceLabel}{Environment.NewLine}" +
                $"分类: {item.ResourceKind}{Environment.NewLine}" +
                $"角色: {item.LibraryRole}{Environment.NewLine}" +
                $"来源类型: {item.SourceType}{Environment.NewLine}" +
                $"来源文件: {item.Source}{Environment.NewLine}" +
                $"PathID: {item.PathId}{Environment.NewLine}" +
                $"Mesh: {item.MeshCount}{Environment.NewLine}" +
                $"顶点: {item.VertexCount}{Environment.NewLine}" +
                $"材质: {item.MaterialCount}{Environment.NewLine}" +
                $"贴图: {item.TextureCount}{Environment.NewLine}" +
                $"骨骼: {item.BoneCount}{Environment.NewLine}" +
                $"已忽略: {_curationStore?.IsIgnored(item)}{Environment.NewLine}" +
                $"文件:{Environment.NewLine}{item.OutputPath}{Environment.NewLine}{Environment.NewLine}" +
                BuildAnimationDetails(animations);

            if (explicitCount == 0)
            {
                _detailBox.AppendText($"{Environment.NewLine}{Environment.NewLine}定向匹配: 正在扫描 animation_bindings.jsonl ...");
                _ = LoadTargetedAnimationDetailsAsync(item, requestId);
            }
        }

        private static string BuildAnimationDetails(IReadOnlyList<LibraryAnimationCandidate> animations)
        {
            if (animations.Count == 0)
            {
                return "显式动画: 0\r\n当前索引没有记录 Animator/Animation 直接绑定。";
            }

            var explicitCount = animations.Count(x => x.IsExplicit);
            var lines = new List<string>
            {
                $"显式动画: {explicitCount}",
                $"索引候选: {animations.Count}"
            };

            foreach (var animation in animations.Take(24))
            {
                var score = animation.Score > 0 ? $" score={animation.Score:0.###}" : "";
                var confidence = string.IsNullOrWhiteSpace(animation.Confidence) ? "" : $" {animation.Confidence}";
                var capability = string.IsNullOrWhiteSpace(animation.Capability) ? "" : $" {animation.Capability}";
                var bake = animation.RequiresHumanoidBake ? " 需要Humanoid烘焙" : "";
                var source = animation.IsExplicit ? "显式" : "结构";
                lines.Add($"- [{source}] {animation.Name}{score}{confidence}{capability}{bake}");
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

                _detailAnimations = targeted.ToList();
                RebuildAnimationList();
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

                foreach (var animation in _detailAnimations.Take(512))
                {
                    var preview = _previewCache?.GetStatus(_detailModel, animation);
                    var status = preview?.Status ?? "未生成";
                    if (animation.RequiresHumanoidBake && string.Equals(status, "未生成", StringComparison.OrdinalIgnoreCase))
                    {
                        status = "需烘焙";
                    }

                    var source = animation.IsExplicit ? "显式" : animation.NeedsValidation ? "需验证" : "候选";
                    var item = new ListViewItem(status)
                    {
                        Tag = animation,
                        ToolTipText = animation.BestPath
                    };
                    item.SubItems.Add(source);
                    item.SubItems.Add(animation.Name);
                    item.SubItems.Add(animation.Duration > 0 ? $"{animation.Duration:0.##}s" : "");
                    item.SubItems.Add(animation.MatchedPathCount > 0 ? animation.MatchedPathCount.ToString() : "");
                    item.SubItems.Add(DescribeAnimationCapability(animation));
                    _animationList.Items.Add(item);
                }
            }
            finally
            {
                _animationList.EndUpdate();
            }
        }

        private static string DescribeAnimationCapability(LibraryAnimationCandidate animation)
        {
            if (animation == null)
            {
                return "";
            }

            if (!string.IsNullOrWhiteSpace(animation.Capability))
            {
                return animation.Capability switch
                {
                    "TransformBodyPreviewReady" => "快速预览",
                    "HumanoidBodyBakeReady" => "需 Unity 烘焙",
                    "BlendShapePreviewReady" => "BlendShape",
                    "NonCharacterTransformPreviewReady" => "Transform 预览",
                    _ => animation.Capability,
                };
            }

            if (animation.RequiresHumanoidBake)
            {
                return "需 Unity 烘焙";
            }

            if (animation.MatchedPathCount > 0)
            {
                return "快速预览";
            }

            return "未知";
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
                var bake = animation.RequiresHumanoidBake ? " 需要Humanoid烘焙" : "";
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

        private void OpenSelectedWithF3d()
        {
            var item = SelectedItems().FirstOrDefault();
            if (item == null)
            {
                return;
            }

            var f3d = FindF3dForOpen();
            var startInfo = new ProcessStartInfo
            {
                FileName = string.IsNullOrWhiteSpace(f3d) ? item.OutputPath : f3d,
                UseShellExecute = true
            };
            if (!string.IsNullOrWhiteSpace(f3d))
            {
                startInfo.ArgumentList.Add(item.OutputPath);
            }

            Process.Start(startInfo);
        }

        private async Task GenerateSelectedAnimationPreviewAsync(bool openAfterGenerate)
        {
            var model = _detailModel ?? SelectedItems().FirstOrDefault();
            var animation = SelectedAnimationCandidate();
            if (model == null || animation == null || _previewCache == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(model.Source) || !File.Exists(model.Source))
            {
                MessageBox.Show(this, "模型源文件不存在，无法重新生成动画预览。\r\n" + model.Source, "动画预览", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(animation.Source) || !File.Exists(animation.Source))
            {
                MessageBox.Show(this, "动画源文件不存在，无法重新生成动画预览。\r\n" + animation.Source, "动画预览", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            UpdateSelectedAnimationStatus("生成中");
            UpdateStatus("正在生成动画预览");
            AnimationPreviewStatus status;
            try
            {
                status = await _previewCache.EnsureAsync(
                    model,
                    animation,
                    CancellationToken.None,
                    message => BeginInvoke(() => UpdateStatus(message)));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "动画预览失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                RebuildAnimationList();
                return;
            }

            RebuildAnimationList();
            UpdateStatus($"动画预览: {status.Status}");
            if (!string.Equals(status.Status, "可播放", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, status.Message ?? "生成动画预览失败。", "动画预览失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (openAfterGenerate && !string.IsNullOrWhiteSpace(status.GltfPath))
            {
                OpenPathWithF3d(status.GltfPath);
            }
        }

        private LibraryAnimationCandidate SelectedAnimationCandidate()
        {
            if (_animationList.SelectedItems.Count > 0)
            {
                return _animationList.SelectedItems[0].Tag as LibraryAnimationCandidate;
            }

            return _detailAnimations.FirstOrDefault();
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
                Arguments = $"/select,\"{item.OutputPath}\"",
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
