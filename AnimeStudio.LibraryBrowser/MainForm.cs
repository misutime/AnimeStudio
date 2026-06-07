using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AnimeStudio.LibraryBrowser
{
    internal sealed class MainForm : Form
    {
        private readonly ToolStrip _toolStrip = new();
        private readonly ToolStripButton _openButton = new("选择素材库");
        private readonly ToolStripDropDownButton _recentButton = new("最近");
        private readonly ToolStripButton _refreshListButton = new("刷新列表");
        private readonly ToolStripButton _reloadButton = new("重新加载");
        private readonly ToolStripTextBox _searchBox = new();
        private readonly ToolStripComboBox _kindBox = new();
        private readonly ToolStripComboBox _thumbnailStateBox = new();
        private readonly ToolStripComboBox _concurrencyBox = new();
        private readonly ToolStripButton _hideIgnoredButton = new("隐藏忽略");
        private readonly SplitContainer _split = new();
        private readonly ListView _modelList = new();
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
        private CancellationTokenSource _thumbnailCts;
        private int _thumbnailTotal;
        private int _thumbnailCached;
        private int _thumbnailFailed;
        private int _thumbnailPending;
        private int _thumbnailActive;
        private bool _listRefreshRequested;
        private bool _statusRefreshRequested;

        public MainForm()
        {
            Text = "AnimeStudio Library Browser";
            Width = 1280;
            Height = 840;
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
            _searchBox.Width = 260;
            _searchBox.ToolTipText = "按名称、路径、来源搜索";
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

            _toolStrip.Items.AddRange(new ToolStripItem[]
            {
                _openButton,
                _recentButton,
                _refreshListButton,
                _reloadButton,
                new ToolStripLabel("搜索"),
                _searchBox,
                new ToolStripLabel("分类"),
                _kindBox,
                new ToolStripLabel("缩略图"),
                _thumbnailStateBox,
                new ToolStripLabel("并发"),
                _concurrencyBox,
                _hideIgnoredButton
            });
            RebuildRecentMenu();
            Controls.Add(_toolStrip);

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

            _split.Dock = DockStyle.Fill;
            _split.SplitterDistance = 900;
            _split.Panel1.Controls.Add(_modelList);
            _split.Panel2.Controls.Add(_detailBox);
            Controls.Add(_split);
            _split.BringToFront();

            _statusStrip.Items.Add(_statusLabel);
            Controls.Add(_statusStrip);

            _kindBox.Items.Add("All");
            _kindBox.SelectedIndex = 0;

            _menu.Items.Add("用 f3d 打开", null, (_, _) => OpenSelectedWithF3d());
            _menu.Items.Add("打开所在目录", null, (_, _) => OpenSelectedFolder());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("加入忽略", null, (_, _) => SetSelectedIgnored(true));
            _menu.Items.Add("取消忽略", null, (_, _) => SetSelectedIgnored(false));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("复制路径", null, (_, _) => CopySelectedPath());
            _modelList.ContextMenuStrip = _menu;
        }

        private void WireEvents()
        {
            _openButton.Click += async (_, _) => await ChooseRootAsync();
            _refreshListButton.Click += (_, _) => RefreshListOnly();
            _reloadButton.Click += async (_, _) => await ReloadAsync();
            _searchBox.TextChanged += (_, _) => ApplyFilter();
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
                _recentStore.Add(root);
                RebuildRecentMenu();
                Text = $"AnimeStudio Library Browser - {Path.GetFileName(root)}";
                ResetThumbnailProgress();
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
            var kind = _kindBox.SelectedItem as string ?? "All";
            IEnumerable<LibraryModelItem> query = _allModels;

            if (!string.Equals(kind, "All", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(x => string.Equals(x.ResourceKind, kind, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                query = query.Where(x =>
                    Contains(x.Name, text)
                    || Contains(x.OutputPath, text)
                    || Contains(x.Source, text)
                    || Contains(x.LibraryRole, text));
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

        private void ModelList_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var item = _visibleModels[e.ItemIndex];
            var imageIndex = GetImageIndex(item);
            e.Item = new ListViewItem(ShortLabel(item))
            {
                ImageIndex = imageIndex,
                ToolTipText = item.OutputPath
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

        private static string ShortLabel(LibraryModelItem item)
        {
            var name = string.IsNullOrWhiteSpace(item.Name) ? item.FileName : item.Name;
            return name.Length <= 34 ? name : name[..31] + "...";
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
            var item = SelectedItems().FirstOrDefault();
            if (item == null)
            {
                _detailBox.Clear();
                return;
            }

            _detailBox.Text =
                $"名称: {item.Name}{Environment.NewLine}" +
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
                $"文件:{Environment.NewLine}{item.OutputPath}";
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
