using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using Advanced_Combat_Tracker;
using FishXIVItemReader.GameClient;
using FishXIVItemReader.Update;
using FishXIVItemReader.Web;

[assembly: AssemblyTitle("鲜鱼背包读取器")]
[assembly: AssemblyDescription("读取你的背包信息，并将背包信息反馈给合作的插件。")]
[assembly: AssemblyCompany("KoiRealm")]
[assembly: AssemblyProduct("FishXIVItemReader")]

namespace FishXIVItemReader
{
    public sealed class PluginMain : UserControl, IActPluginV1
    {
        private const string PluginTabTitle = "鲜鱼背包读取器";
        private const string EmbeddedUpdaterResourceName = "FishXIVItemReader.Update.FishXIVItemReader.Updater.exe";

        private readonly Label processLabel;
        private readonly ComboBox processComboBox;
        private readonly Label readModeLabel;
        private readonly ComboBox readModeComboBox;
        private readonly CheckBox includeSaddleBagCheckBox;
        private readonly CheckBox includeRetainerStorageCheckBox;
        private readonly CheckBox includeArmoryChestCheckBox;
        private readonly CheckBox includeEquippedItemsCheckBox;
        private readonly CheckBox debugModeCheckBox;
        private readonly Button refreshProcessesButton;
        private readonly Button switchWindowButton;
        private readonly Button readInventoryButton;
        private readonly Label webSocketPortLabel;
        private readonly NumericUpDown webSocketPortNumericUpDown;
        private readonly Button applyWebSocketPortButton;
        private readonly GroupBox statusGroupBox;
        private readonly Label statusStateValueLabel;
        private readonly Label statusInventoryValueLabel;
        private readonly Label statusWebSocketValueLabel;
        private readonly Label statusOverlayPluginValueLabel;
        private readonly GroupBox updateGroupBox;
        private readonly Label updateCurrentValueLabel;
        private readonly Label updateLatestValueLabel;
        private readonly Label updateStatusValueLabel;
        private readonly Button checkUpdateButton;
        private readonly Button downloadUpdateButton;
        private readonly GroupBox networkDetailGroupBox;
        private readonly Label networkConnectionValueLabel;
        private readonly Label networkProcessValueLabel;
        private readonly Label networkPacketsValueLabel;
        private readonly Label networkInventoryValueLabel;
        private readonly Label networkCacheValueLabel;
        private readonly Label networkLastPacketValueLabel;
        private readonly Label networkTimesValueLabel;
        private readonly Label networkErrorValueLabel;
        private readonly DataGridView inventoryGrid;
        private readonly DeucalionInventoryReader networkInventoryReader;
        private readonly InventoryWebSocketServer inventoryWebSocketServer;
        private readonly OverlayPluginEventBridge overlayPluginEventBridge;
        private readonly PluginUpdateService pluginUpdateService;
        private readonly object autoReadExecutionGate = new object();

        private Label statusLabel;
        private SettingsSerializer settings;
        private readonly string settingsFile;
        private readonly string updateDownloadDirectory;
        private readonly string pluginDirectory;
        private CancellationTokenSource autoReadCancellation;
        private Task autoReadTask;
        private CancellationTokenSource updateCancellation;
        private PluginUpdateCheckResult latestUpdateCheckResult;
        private string pendingUpdaterExecutablePath;
        private string pendingUpdateStagingDirectory;
        private int pendingUpdaterProcessId;
        private int autoReadVersion;
        private int activeAutoReadProcessId;
        private InventoryReadMode activeAutoReadMode;
        private bool updateOperationRunning;
        private bool pluginInitialized;
        private bool suppressAutoRestart;
        private string lastAutoReadError;
        private string lastInventoryGridSignature;
        private int configuredWebSocketPort = InventoryWebSocketServer.DefaultPort;

        public PluginMain()
        {
            settingsFile = Path.Combine(
                ActGlobals.oFormActMain.AppDataFolder.FullName,
                "Config",
                "FishXIVItemReader.config.xml");
            updateDownloadDirectory = Path.Combine(
                ActGlobals.oFormActMain.AppDataFolder.FullName,
                "Config",
                "FishXIVItemReader",
                "Updates");
            pluginDirectory = ResolvePluginDirectory();
            pluginUpdateService = new PluginUpdateService();

            processLabel = new Label
            {
                AutoSize = true,
                Location = new Point(18, 20),
                Text = "游戏进程"
            };

            processComboBox = new ComboBox
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(108, 16),
                Size = new Size(300, 24)
            };
            processComboBox.SelectedIndexChanged += delegate
            {
                UpdateSwitchWindowButton();
                RestartAutoRead();
            };

            readModeLabel = new Label
            {
                AutoSize = true,
                Location = new Point(18, 52),
                Text = "读取模式"
            };

            readModeComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(108, 48),
                Size = new Size(180, 24)
            };
            readModeComboBox.Items.Add(new ReadModeOption(InventoryReadMode.Memory, "内存模式"));
            readModeComboBox.Items.Add(new ReadModeOption(InventoryReadMode.Network, "网络模式"));
            readModeComboBox.SelectedIndex = 0;
            readModeComboBox.SelectedIndexChanged += delegate
            {
                UpdateModeHint();
                RestartAutoRead();
            };

            includeSaddleBagCheckBox = new CheckBox
            {
                AutoSize = true,
                Location = new Point(18, 80),
                Text = "读取陆行鸟鞍囊"
            };
            includeSaddleBagCheckBox.CheckedChanged += delegate { RestartAutoRead(); };

            includeRetainerStorageCheckBox = new CheckBox
            {
                AutoSize = true,
                Location = new Point(160, 80),
                Text = "读取雇员仓库"
            };
            includeRetainerStorageCheckBox.CheckedChanged += delegate { RestartAutoRead(); };

            includeArmoryChestCheckBox = new CheckBox
            {
                AutoSize = true,
                Location = new Point(282, 80),
                Text = "读取兵装库"
            };
            includeArmoryChestCheckBox.CheckedChanged += delegate { RestartAutoRead(); };

            includeEquippedItemsCheckBox = new CheckBox
            {
                AutoSize = true,
                Location = new Point(390, 80),
                Text = "读取当前装备"
            };
            includeEquippedItemsCheckBox.CheckedChanged += delegate { RestartAutoRead(); };

            debugModeCheckBox = new CheckBox
            {
                AutoSize = true,
                Location = new Point(520, 80),
                Text = "调试模式"
            };
            debugModeCheckBox.CheckedChanged += delegate { UpdateDebugGridVisibility(); };

            refreshProcessesButton = new Button
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(420, 14),
                Size = new Size(92, 28),
                Text = "刷新"
            };
            refreshProcessesButton.Click += delegate { RefreshProcessList(); };

            switchWindowButton = new Button
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Enabled = false,
                Location = new Point(520, 14),
                Size = new Size(96, 28),
                Text = "切换窗口"
            };
            switchWindowButton.Click += delegate { SwitchToSelectedGameWindow(); };

            readInventoryButton = new Button
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(624, 14),
                Size = new Size(120, 28),
                Text = "重启读取"
            };
            readInventoryButton.Click += delegate { RestartAutoRead(); };

            webSocketPortLabel = new Label
            {
                AutoSize = true,
                Location = new Point(300, 52),
                Text = "WS端口"
            };

            webSocketPortNumericUpDown = new NumericUpDown
            {
                Location = new Point(356, 48),
                Maximum = 65535,
                Minimum = 1,
                Size = new Size(76, 24),
                Value = InventoryWebSocketServer.DefaultPort
            };

            applyWebSocketPortButton = new Button
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(440, 46),
                Size = new Size(76, 28),
                Text = "应用"
            };
            applyWebSocketPortButton.Click += delegate { ApplyWebSocketPortSetting(); };

            statusGroupBox = new GroupBox
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Location = new Point(18, 106),
                Size = new Size(648, 104),
                Text = "状态"
            };
            var statusTable = new TableLayoutPanel
            {
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 8, 8, 6),
                RowCount = 4
            };
            statusTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            statusTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var row = 0; row < 4; row++)
            {
                statusTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 17));
            }

            Label stateValueLabel;
            Label inventoryValueLabel;
            Label webSocketValueLabel;
            Label overlayPluginValueLabel;
            AddStatusRow(statusTable, 0, "状态", out stateValueLabel);
            AddStatusRow(statusTable, 1, "库存", out inventoryValueLabel);
            AddStatusRow(statusTable, 2, "WS服务", out webSocketValueLabel);
            AddStatusRow(statusTable, 3, "Overlay", out overlayPluginValueLabel);
            statusStateValueLabel = stateValueLabel;
            statusInventoryValueLabel = inventoryValueLabel;
            statusWebSocketValueLabel = webSocketValueLabel;
            statusOverlayPluginValueLabel = overlayPluginValueLabel;
            statusGroupBox.Controls.Add(statusTable);
            SetStatusPanel("未启动", "-");
            SetStatusWebSocket("未启动", null);
            SetStatusOverlayPlugin("未连接", null);

            updateGroupBox = new GroupBox
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Location = new Point(18, 178),
                Size = new Size(648, 100),
                Text = "更新"
            };
            var updateTable = new TableLayoutPanel
            {
                ColumnCount = 4,
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 8, 8, 6),
                RowCount = 3
            };
            updateTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            updateTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            updateTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
            updateTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            for (var row = 0; row < 3; row++)
            {
                updateTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            }

            Label currentVersionLabel;
            Label latestVersionLabel;
            Label updateStatusLabel;
            AddStatusRow(updateTable, 0, "当前", out currentVersionLabel);
            AddStatusRow(updateTable, 1, "最新", out latestVersionLabel);
            AddStatusRow(updateTable, 2, "状态", out updateStatusLabel);
            updateCurrentValueLabel = currentVersionLabel;
            updateLatestValueLabel = latestVersionLabel;
            updateStatusValueLabel = updateStatusLabel;
            updateCurrentValueLabel.Text = pluginUpdateService.CurrentVersionText;
            updateLatestValueLabel.Text = "-";
            updateStatusValueLabel.Text = "未检查";

            checkUpdateButton = new Button
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(6, 0, 0, 2),
                Text = "检查更新"
            };
            checkUpdateButton.Click += async delegate { await CheckPluginUpdateAsync(); };

            downloadUpdateButton = new Button
            {
                Dock = DockStyle.Fill,
                Enabled = false,
                Margin = new Padding(6, 0, 0, 2),
                Text = "下载并安装"
            };
            downloadUpdateButton.Click += async delegate { await DownloadPluginUpdateAsync(); };
            updateTable.Controls.Add(checkUpdateButton, 2, 0);
            updateTable.SetRowSpan(checkUpdateButton, 2);
            updateTable.Controls.Add(downloadUpdateButton, 3, 0);
            updateTable.SetRowSpan(downloadUpdateButton, 2);
            updateGroupBox.Controls.Add(updateTable);

            networkDetailGroupBox = new GroupBox
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Location = new Point(18, 286),
                Size = new Size(648, 176),
                Text = "网络模式详情",
                Visible = false
            };
            var networkTable = new TableLayoutPanel
            {
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 8, 8, 6),
                RowCount = 8
            };
            networkTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            networkTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var row = 0; row < 8; row++)
            {
                networkTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 17));
            }

            Label networkConnectionLabel;
            Label networkProcessLabel;
            Label networkPacketsLabel;
            Label networkInventoryLabel;
            Label networkCacheLabel;
            Label networkLastPacketLabel;
            Label networkTimesLabel;
            Label networkErrorLabel;
            AddStatusRow(networkTable, 0, "连接", out networkConnectionLabel);
            AddStatusRow(networkTable, 1, "PID", out networkProcessLabel);
            AddStatusRow(networkTable, 2, "包统计", out networkPacketsLabel);
            AddStatusRow(networkTable, 3, "背包", out networkInventoryLabel);
            AddStatusRow(networkTable, 4, "缓存", out networkCacheLabel);
            AddStatusRow(networkTable, 5, "最后包", out networkLastPacketLabel);
            AddStatusRow(networkTable, 6, "最近时间", out networkTimesLabel);
            AddStatusRow(networkTable, 7, "错误", out networkErrorLabel);
            networkConnectionValueLabel = networkConnectionLabel;
            networkProcessValueLabel = networkProcessLabel;
            networkPacketsValueLabel = networkPacketsLabel;
            networkInventoryValueLabel = networkInventoryLabel;
            networkCacheValueLabel = networkCacheLabel;
            networkLastPacketValueLabel = networkLastPacketLabel;
            networkTimesValueLabel = networkTimesLabel;
            networkErrorValueLabel = networkErrorLabel;
            networkDetailGroupBox.Controls.Add(networkTable);
            ResetNetworkDetailPanel();

            inventoryGrid = new DataGridView
            {
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                Location = new Point(18, 244),
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                Size = new Size(648, 260),
                Visible = false
            };
            ConfigureGrid();

            AutoScaleMode = AutoScaleMode.Font;
            Controls.Add(processLabel);
            Controls.Add(processComboBox);
            Controls.Add(readModeLabel);
            Controls.Add(readModeComboBox);
            Controls.Add(includeSaddleBagCheckBox);
            Controls.Add(includeRetainerStorageCheckBox);
            Controls.Add(includeArmoryChestCheckBox);
            Controls.Add(includeEquippedItemsCheckBox);
            Controls.Add(debugModeCheckBox);
            Controls.Add(refreshProcessesButton);
            Controls.Add(switchWindowButton);
            Controls.Add(readInventoryButton);
            Controls.Add(webSocketPortLabel);
            Controls.Add(webSocketPortNumericUpDown);
            Controls.Add(applyWebSocketPortButton);
            Controls.Add(statusGroupBox);
            Controls.Add(updateGroupBox);
            Controls.Add(networkDetailGroupBox);
            Controls.Add(inventoryGrid);
            Name = "FishXIVItemReader";
            Size = new Size(700, 420);
            AutoScroll = true;
            networkInventoryReader = new DeucalionInventoryReader();
            inventoryWebSocketServer = new InventoryWebSocketServer();
            overlayPluginEventBridge = new OverlayPluginEventBridge();
            inventoryWebSocketServer.MessagePublished += overlayPluginEventBridge.PublishJson;
            inventoryWebSocketServer.ClientCountChanged += RefreshWebSocketStatusPanel;
            overlayPluginEventBridge.StatusChanged += SetStatusOverlayPluginFromWorker;
            Resize += delegate { LayoutControls(); };
            LayoutControls();
        }

        public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        {
            statusLabel = pluginStatusText;
            settings = new SettingsSerializer(this);

            pluginScreenSpace.Controls.Add(this);
            pluginScreenSpace.Text = PluginTabTitle;
            Dock = DockStyle.Fill;

            LoadSettings();
            CleanupPendingUpdaterExecutable();
            pluginInitialized = true;
            SetStatus(PluginTabTitle + "已启动");
            StartInventoryWebSocketServer();
            RefreshProcessList();
            if (!ResumePendingPreparedUpdate())
            {
                _ = CheckPluginUpdateAsync();
            }
        }

        public void DeInitPlugin()
        {
            pluginInitialized = false;
            StartPendingUpdateInstallerForShutdown();
            SaveSettings();
            CancelUpdateOperation();
            StopAutoRead(stopNetworkReader: true);
            inventoryWebSocketServer.ClientCountChanged -= RefreshWebSocketStatusPanel;
            inventoryWebSocketServer.MessagePublished -= overlayPluginEventBridge.PublishJson;
            overlayPluginEventBridge.StatusChanged -= SetStatusOverlayPluginFromWorker;
            networkInventoryReader.Dispose();
            inventoryWebSocketServer.Dispose();
            overlayPluginEventBridge.Dispose();

            if (statusLabel != null)
                statusLabel.Text = PluginTabTitle + "已退出";
        }

        private void ConfigureGrid()
        {
            inventoryGrid.Columns.Add(CreateTextColumn("Container", "背包页", 110));
            inventoryGrid.Columns.Add(CreateTextColumn("Slot", "槽位", 56));
            inventoryGrid.Columns.Add(CreateTextColumn("ItemId", "物品 ID", 90));
            inventoryGrid.Columns.Add(CreateTextColumn("Quantity", "数量", 70));
            inventoryGrid.Columns.Add(CreateTextColumn("HQ", "高品质", 64));
            inventoryGrid.Columns.Add(CreateTextColumn("Collectable", "收藏品", 78));
            inventoryGrid.Columns.Add(CreateTextColumn("Condition", "耐久/收藏", 90));
            inventoryGrid.Columns.Add(CreateTextColumn("Flags", "标记", 120));
        }

        private static DataGridViewTextBoxColumn CreateTextColumn(string name, string headerText, int width)
        {
            return new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = headerText,
                Width = width,
                FillWeight = width
            };
        }

        private static void AddStatusRow(TableLayoutPanel table, int row, string name, out Label valueLabel)
        {
            var nameLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = name,
                TextAlign = ContentAlignment.MiddleLeft
            };
            valueLabel = new Label
            {
                AutoEllipsis = true,
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = "-",
                TextAlign = ContentAlignment.MiddleLeft
            };

            table.Controls.Add(nameLabel, 0, row);
            table.Controls.Add(valueLabel, 1, row);
        }

        private void SetStatusPanel(string state, string inventory)
        {
            statusStateValueLabel.Text = string.IsNullOrWhiteSpace(state) ? "-" : state;
            statusInventoryValueLabel.Text = string.IsNullOrWhiteSpace(inventory) ? "-" : inventory;
        }

        private void SetStatusState(string state, string detail)
        {
            statusStateValueLabel.Text = string.IsNullOrWhiteSpace(state) ? "-" : state;
            SetStatus(string.IsNullOrWhiteSpace(detail) ? state : detail);
        }

        private void SetStatusDetail(string detail)
        {
            SetStatus(detail);
        }

        private void SetStatusWebSocket(string webSocket, string detail)
        {
            statusWebSocketValueLabel.Text = string.IsNullOrWhiteSpace(webSocket) ? "-" : webSocket;
            if (!string.IsNullOrWhiteSpace(detail))
            {
                SetStatus(detail);
            }
        }

        private void SetStatusOverlayPlugin(string overlayPlugin, string detail)
        {
            statusOverlayPluginValueLabel.Text = FormatOverlayPluginStatusText(overlayPlugin);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                SetStatus(detail);
            }
        }

        private void SetStatusOverlayPluginFromWorker(string overlayPlugin)
        {
            RunOnUiThread(delegate
            {
                SetStatusOverlayPlugin(overlayPlugin, null);
            });
        }

        private void RefreshWebSocketStatusPanel()
        {
            RunOnUiThread(delegate
            {
                statusWebSocketValueLabel.Text = BuildWebSocketStatusText();
            });
        }

        private string BuildWebSocketStatusText()
        {
            if (!inventoryWebSocketServer.IsRunning)
            {
                return "未启动";
            }

            return string.Format(
                "客户端 {0}",
                inventoryWebSocketServer.ClientCount);
        }

        private static string FormatOverlayPluginStatusText(string overlayPlugin)
        {
            if (string.IsNullOrWhiteSpace(overlayPlugin))
            {
                return "-";
            }

            if (overlayPlugin.StartsWith("已连接", StringComparison.Ordinal))
            {
                return "已连接";
            }

            if (overlayPlugin.StartsWith("未连接", StringComparison.Ordinal))
            {
                return "未连接";
            }

            if (overlayPlugin.StartsWith("连接异常", StringComparison.Ordinal))
            {
                return "连接异常";
            }

            return overlayPlugin;
        }

        private void RunOnUiThread(Action action)
        {
            if (action == null || IsDisposed)
            {
                return;
            }

            if (!InvokeRequired)
            {
                action();
                return;
            }

            try
            {
                BeginInvoke(action);
            }
            catch
            {
            }
        }

        private void SetNetworkDetailVisibility(bool visible)
        {
            if (networkDetailGroupBox.Visible == visible)
            {
                return;
            }

            networkDetailGroupBox.Visible = visible;
            if (!visible)
            {
                ResetNetworkDetailPanel();
            }

            if (inventoryGrid != null)
            {
                LayoutControls();
            }
        }

        private bool ShouldShowNetworkDetailPanel()
        {
            return ShouldShowNetworkDetailPanel(GetSelectedReadMode());
        }

        private bool ShouldShowNetworkDetailPanel(InventoryReadMode mode)
        {
            return debugModeCheckBox.Checked && mode == InventoryReadMode.Network;
        }

        private void ResetNetworkDetailPanel()
        {
            networkConnectionValueLabel.Text = "-";
            networkProcessValueLabel.Text = "-";
            networkPacketsValueLabel.Text = "-";
            networkInventoryValueLabel.Text = "-";
            networkCacheValueLabel.Text = "-";
            networkLastPacketValueLabel.Text = "-";
            networkTimesValueLabel.Text = "-";
            networkErrorValueLabel.Text = "-";
        }

        private void UpdateNetworkDetailPanel(DeucalionCaptureStatus captureStatus)
        {
            if (!ShouldShowNetworkDetailPanel())
            {
                SetNetworkDetailVisibility(false);
                return;
            }

            if (captureStatus == null)
            {
                ResetNetworkDetailPanel();
                return;
            }

            SetNetworkDetailVisibility(true);
            networkConnectionValueLabel.Text = captureStatus.Connected ? "已连接 Deucalion" : "未连接 Deucalion";
            networkProcessValueLabel.Text = captureStatus.ProcessId > 0
                ? captureStatus.ProcessId.ToString()
                : "-";
            networkPacketsValueLabel.Text = string.Format(
                "Zone {0} / 已识别 {1}",
                captureStatus.ZonePacketsSeen,
                captureStatus.RecognizedPacketsSeen);
            networkInventoryValueLabel.Text = string.Format(
                "背包包 {0} / 快照更新 {1}",
                captureStatus.InventoryPacketsSeen,
                captureStatus.InventorySnapshotUpdates);
            networkCacheValueLabel.Text = string.Format(
                "缓存物品 {0} / 队列 {1}",
                captureStatus.CachedItems,
                captureStatus.QueuedItemInfos);
            networkLastPacketValueLabel.Text = FormatNetworkLastPacket(captureStatus);
            networkTimesValueLabel.Text = string.Format(
                "Zone {0} / 已识别 {1} / 背包 {2}",
                FormatCaptureTime(captureStatus.LastZonePacketAt),
                FormatCaptureTime(captureStatus.LastRecognizedPacketAt),
                FormatCaptureTime(captureStatus.LastInventoryPacketAt));
            networkErrorValueLabel.Text = string.IsNullOrWhiteSpace(captureStatus.LastError)
                ? "-"
                : captureStatus.LastError;
        }

        private void TryUpdateNetworkDetailPanel()
        {
            try
            {
                UpdateNetworkDetailPanel(networkInventoryReader.GetCaptureStatus());
            }
            catch
            {
            }
        }

        private static string FormatNetworkLastPacket(DeucalionCaptureStatus captureStatus)
        {
            if (captureStatus == null || captureStatus.LastOpcode <= 0)
            {
                return "-";
            }

            var text = string.Format(
                "{0} {1}",
                string.IsNullOrWhiteSpace(captureStatus.LastPacketDirection) ? "?" : captureStatus.LastPacketDirection,
                captureStatus.LastOpcode);
            return string.IsNullOrWhiteSpace(captureStatus.LastPacketType)
                ? text
                : text + " / " + captureStatus.LastPacketType;
        }

        private static string FormatCaptureTime(DateTime value)
        {
            return value == default(DateTime)
                ? "-"
                : value.ToString("HH:mm:ss");
        }

        private void RefreshProcessList()
        {
            suppressAutoRestart = true;
            var selectedProcessId = processComboBox.SelectedItem is ClientProcessInfo selected
                ? selected.ProcessId
                : 0;

            processComboBox.Items.Clear();

            foreach (var process in ClientInventoryReader.FindCandidateProcesses())
            {
                processComboBox.Items.Add(process);
                if (process.ProcessId == selectedProcessId)
                    processComboBox.SelectedItem = process;
            }

            if (processComboBox.SelectedIndex < 0 && processComboBox.Items.Count > 0)
                processComboBox.SelectedIndex = 0;

            suppressAutoRestart = false;

            if (processComboBox.Items.Count == 0)
            {
                StopAutoRead(stopNetworkReader: true);
                UpdateSwitchWindowButton();
                SetStatusPanel("未找到进程", "-");
                SetStatus("未找到 FFXIV 进程。");
            }
            else
            {
                UpdateSwitchWindowButton();
                SetStatusState("已找到进程", string.Format("已找到 {0} 个 FFXIV 进程。", processComboBox.Items.Count));
                UpdateModeHint();
                RestartAutoRead();
            }
        }

        private void LayoutControls()
        {
            const int margin = 18;
            const int gap = 8;
            const int refreshWidth = 92;
            const int switchWindowWidth = 96;
            const int readWidth = 120;
            const int applyPortWidth = 76;
            const int portInputWidth = 76;
            const int portLabelWidth = 58;
            const int buttonHeight = 28;

            var right = Math.Max(Width - margin, 680);
            var readLeft = right - readWidth;
            var switchWindowLeft = readLeft - gap - switchWindowWidth;
            var refreshLeft = switchWindowLeft - gap - refreshWidth;
            var comboRight = refreshLeft - gap;
            var applyPortLeft = right - applyPortWidth;
            var portInputLeft = applyPortLeft - gap - portInputWidth;
            var portLabelLeft = portInputLeft - gap - portLabelWidth;

            processComboBox.Width = Math.Max(220, comboRight - processComboBox.Left);
            readModeComboBox.Width = Math.Max(160, Math.Min(240, portLabelLeft - gap - readModeComboBox.Left));
            webSocketPortLabel.Location = new Point(portLabelLeft, 52);
            webSocketPortNumericUpDown.Location = new Point(portInputLeft, 48);
            webSocketPortNumericUpDown.Size = new Size(portInputWidth, 24);
            applyWebSocketPortButton.Location = new Point(applyPortLeft, 46);
            applyWebSocketPortButton.Size = new Size(applyPortWidth, buttonHeight);
            debugModeCheckBox.Location = new Point(applyPortLeft, 80);
            refreshProcessesButton.Location = new Point(refreshLeft, 14);
            refreshProcessesButton.Size = new Size(refreshWidth, buttonHeight);
            switchWindowButton.Location = new Point(switchWindowLeft, 14);
            switchWindowButton.Size = new Size(switchWindowWidth, buttonHeight);
            readInventoryButton.Location = new Point(readLeft, 14);
            readInventoryButton.Size = new Size(readWidth, buttonHeight);
            statusGroupBox.Width = Math.Max(320, right - statusGroupBox.Left);
            updateGroupBox.Width = Math.Max(320, right - updateGroupBox.Left);
            updateGroupBox.Location = new Point(margin, statusGroupBox.Bottom + gap);
            networkDetailGroupBox.Width = Math.Max(320, right - networkDetailGroupBox.Left);
            networkDetailGroupBox.Location = new Point(margin, updateGroupBox.Bottom + gap);
            var gridTop = networkDetailGroupBox.Visible
                ? networkDetailGroupBox.Bottom + gap
                : updateGroupBox.Bottom + gap;
            inventoryGrid.Location = new Point(margin, gridTop);
            inventoryGrid.Width = Math.Max(320, right - inventoryGrid.Left);
            inventoryGrid.Height = Math.Max(180, Height - inventoryGrid.Top - margin);

            refreshProcessesButton.BringToFront();
            switchWindowButton.BringToFront();
            readInventoryButton.BringToFront();
            webSocketPortLabel.BringToFront();
            webSocketPortNumericUpDown.BringToFront();
            applyWebSocketPortButton.BringToFront();
            debugModeCheckBox.BringToFront();
        }

        private async Task CheckPluginUpdateAsync()
        {
            if (!BeginUpdateOperation("正在检查更新..."))
            {
                return;
            }

            latestUpdateCheckResult = null;
            updateLatestValueLabel.Text = "-";

            try
            {
                var result = await pluginUpdateService.CheckAsync(updateCancellation.Token);
                latestUpdateCheckResult = result;
                updateLatestValueLabel.Text = PluginUpdateService.FormatVersion(result.LatestVersion);
                updateStatusValueLabel.Text = result.UpdateAvailable
                    ? "发现新版本，可以下载并安装。"
                    : "已是最新版本。";
                SetStatus(result.UpdateAvailable
                    ? "发现 FishXIVItemReader 新版本。"
                    : "FishXIVItemReader 已是最新版本。");
            }
            catch (OperationCanceledException)
            {
                updateStatusValueLabel.Text = "已取消。";
            }
            catch (Exception)
            {
                updateStatusValueLabel.Text = "检查更新失败。";
                SetStatus("检查 FishXIVItemReader 更新失败。");
            }
            finally
            {
                EndUpdateOperation();
            }
        }

        private async Task DownloadPluginUpdateAsync()
        {
            if (latestUpdateCheckResult == null || !latestUpdateCheckResult.UpdateAvailable)
            {
                await CheckPluginUpdateAsync();
                if (latestUpdateCheckResult == null || !latestUpdateCheckResult.UpdateAvailable)
                {
                    return;
                }
            }

            if (!BeginUpdateOperation("正在下载更新..."))
            {
                return;
            }

            var update = latestUpdateCheckResult;
            try
            {
                var updatePath = await pluginUpdateService.DownloadUpdateAsync(
                    update.Manifest,
                    updateDownloadDirectory,
                    updateCancellation.Token);
                updateStatusValueLabel.Text = "正在准备更新...";

                var preparedInstall = await pluginUpdateService.PrepareUpdateInstallAsync(
                    updatePath,
                    updateDownloadDirectory,
                    updateCancellation.Token);
                updateStatusValueLabel.Text = string.Format(
                    "已准备 {0} 个文件，重启 ACT 后自动安装。",
                    preparedInstall.PreparedFileCount);
                SetStatus("FishXIVItemReader 更新已准备，重启 ACT 后自动安装。");
                PromptActRestart(preparedInstall);
            }
            catch (OperationCanceledException)
            {
                updateStatusValueLabel.Text = "已取消。";
            }
            catch (Exception)
            {
                updateStatusValueLabel.Text = "准备更新失败。";
                SetStatus("准备 FishXIVItemReader 更新失败。");
            }
            finally
            {
                EndUpdateOperation();
            }
        }

        private void PromptActRestart(PluginUpdatePreparedInstallResult preparedInstall)
        {
            SavePendingUpdateStagingDirectory(preparedInstall.StagingDirectory);
            if (TryInstallPreparedUpdateNow(preparedInstall))
            {
                return;
            }

            updateStatusValueLabel.Text = "直接安装失败，将在重启 ACT 时安装。";
            SetStatus("FishXIVItemReader 直接覆盖更新失败，已切换为重启后安装。");

            var response = MessageBox.Show(
                this,
                "FishXIVItemReader 更新已准备，但当前插件文件无法直接覆盖，需要关闭 ACT 后安装。" + Environment.NewLine + "是否现在重启 ACT？",
                "FishXIVItemReader 更新",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1);
            if (response != DialogResult.Yes)
            {
                updateStatusValueLabel.Text = "更新已准备，尚未重启。";
                SetStatus("FishXIVItemReader 更新已准备，尚未启动安装器。");
                return;
            }

            try
            {
                StartDeferredUpdateInstaller(preparedInstall.StagingDirectory, true);
                latestUpdateCheckResult = null;
                updateStatusValueLabel.Text = "正在重启 ACT 以完成安装...";
                SetStatus("正在重启 ACT 以完成 FishXIVItemReader 更新。");
                CloseActForUpdate();
            }
            catch (Exception)
            {
                updateStatusValueLabel.Text = "启动安装器失败。";
                SetStatus("启动 FishXIVItemReader 更新安装器失败。");
            }
        }

        private void StartDeferredUpdateInstaller(string stagingDirectory, bool restartActAfterCopy)
        {
            if (string.IsNullOrWhiteSpace(stagingDirectory) || !Directory.Exists(stagingDirectory))
            {
                throw new DirectoryNotFoundException("更新暂存目录不存在。");
            }

            SavePendingUpdateStagingDirectory(stagingDirectory);
            var updaterPath = ExtractEmbeddedUpdaterExecutable();
            var logPath = Path.Combine(updateDownloadDirectory, "ApplyFishXIVItemReaderUpdate.log");
            var actExecutablePath = GetCurrentProcessExecutablePath();
            var arguments =
                "--act-pid " + Process.GetCurrentProcess().Id +
                " --plugin-dir " + QuoteCommandLineArgument(pluginDirectory) +
                " --staging-dir " + QuoteCommandLineArgument(stagingDirectory) +
                " --act-exe " + QuoteCommandLineArgument(actExecutablePath) +
                " --restart-act " + (restartActAfterCopy ? "true" : "false") +
                " --log " + QuoteCommandLineArgument(logPath);

            var startInfo = new ProcessStartInfo(updaterPath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            SavePendingUpdaterExecutablePath(updaterPath);
            try
            {
                var process = Process.Start(startInfo);
                if (process == null)
                {
                    throw new InvalidOperationException("无法启动更新安装器。");
                }

                pendingUpdaterProcessId = process.Id;
            }
            catch
            {
                SavePendingUpdaterExecutablePath(string.Empty);
                throw;
            }
        }

        private bool TryInstallPreparedUpdateNow(PluginUpdatePreparedInstallResult preparedInstall)
        {
            try
            {
                pluginUpdateService.InstallPreparedUpdate(
                    preparedInstall.StagingDirectory,
                    pluginDirectory,
                    CancellationToken.None);
                SavePendingUpdateStagingDirectory(string.Empty);
                latestUpdateCheckResult = null;
                updateStatusValueLabel.Text = "更新已安装，等待重启 ACT...";
                SetStatus("FishXIVItemReader 更新已安装，重启 ACT 后生效。");
                RequestActRestartForInstalledUpdate();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void RequestActRestartForInstalledUpdate()
        {
            var restartMethod = FindActRestartMethod();
            if (restartMethod != null &&
                TryRequestActRestart(
                    restartMethod,
                    "FishXIVItemReader 更新已安装，重启 ACT 后生效。"))
            {
                return;
            }

            MessageBox.Show(
                this,
                "FishXIVItemReader 更新已安装，请重启 ACT 后生效。",
                "FishXIVItemReader 更新",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private string ExtractEmbeddedUpdaterExecutable()
        {
            Directory.CreateDirectory(updateDownloadDirectory);
            var updaterPath = Path.Combine(
                updateDownloadDirectory,
                "FishXIVItemReader.Updater." + Guid.NewGuid().ToString("N") + ".exe");

            using (var input = typeof(PluginMain).Assembly.GetManifestResourceStream(EmbeddedUpdaterResourceName))
            {
                if (input == null)
                {
                    throw new InvalidOperationException("内嵌更新安装器不存在。");
                }

                using (var output = new FileStream(updaterPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    input.CopyTo(output);
                }
            }

            return updaterPath;
        }

        private static string GetCurrentProcessExecutablePath()
        {
            if (!string.IsNullOrWhiteSpace(Application.ExecutablePath))
            {
                return Application.ExecutablePath;
            }

            try
            {
                var module = Process.GetCurrentProcess().MainModule;
                return module == null ? string.Empty : module.FileName;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static MethodInfo FindActRestartMethod()
        {
            var form = ActGlobals.oFormActMain;
            if (form == null)
            {
                return null;
            }

            var methods = form.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var method in methods)
            {
                if (!string.Equals(method.Name, "RestartACT", StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(bool) &&
                    parameters[1].ParameterType == typeof(string))
                {
                    return method;
                }
            }

            foreach (var method in methods)
            {
                if (!string.Equals(method.Name, "RestartACT", StringComparison.Ordinal))
                {
                    continue;
                }

                if (method.GetParameters().Length == 0)
                {
                    return method;
                }
            }

            return null;
        }

        private static bool TryRequestActRestart(MethodInfo restartMethod, string additionalInfo)
        {
            var form = ActGlobals.oFormActMain;
            if (form == null || restartMethod == null)
            {
                return false;
            }

            try
            {
                var parameters = restartMethod.GetParameters();
                var arguments = parameters.Length == 2
                    ? new object[] { true, additionalInfo }
                    : new object[0];
                restartMethod.Invoke(form, arguments);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string QuoteCommandLineArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private void SavePendingUpdaterExecutablePath(string updaterPath)
        {
            pendingUpdaterExecutablePath = updaterPath ?? string.Empty;
            SaveSettings();
        }

        private void SavePendingUpdateStagingDirectory(string stagingDirectory)
        {
            pendingUpdateStagingDirectory = stagingDirectory ?? string.Empty;
            SaveSettings();
        }

        private void CleanupPendingUpdaterExecutable()
        {
            if (string.IsNullOrWhiteSpace(pendingUpdaterExecutablePath))
            {
                return;
            }

            var pendingPath = pendingUpdaterExecutablePath;
            if (!IsSafePendingUpdaterExecutablePath(pendingPath))
            {
                SavePendingUpdaterExecutablePath(string.Empty);
                return;
            }

            if (!File.Exists(pendingPath))
            {
                SavePendingUpdaterExecutablePath(string.Empty);
                return;
            }

            if (TryDeletePendingUpdaterExecutable(pendingPath))
            {
                SavePendingUpdaterExecutablePath(string.Empty);
            }
        }

        private bool ResumePendingPreparedUpdate()
        {
            if (string.IsNullOrWhiteSpace(pendingUpdateStagingDirectory))
            {
                return false;
            }

            var stagingDirectory = pendingUpdateStagingDirectory;
            if (!IsSafePendingUpdateStagingDirectory(stagingDirectory) || !Directory.Exists(stagingDirectory))
            {
                SavePendingUpdateStagingDirectory(string.Empty);
                return false;
            }

            updateStatusValueLabel.Text = "检测到待完成更新，请关闭 ACT 后完成安装。";
            SetStatus("检测到 FishXIVItemReader 待完成更新，关闭 ACT 后将继续安装。");
            return true;
        }

        private void StartPendingUpdateInstallerForShutdown()
        {
            if (string.IsNullOrWhiteSpace(pendingUpdateStagingDirectory) ||
                !IsSafePendingUpdateStagingDirectory(pendingUpdateStagingDirectory) ||
                !Directory.Exists(pendingUpdateStagingDirectory) ||
                IsPendingUpdaterProcessRunning())
            {
                return;
            }

            try
            {
                StartDeferredUpdateInstaller(pendingUpdateStagingDirectory, false);
            }
            catch
            {
            }
        }

        private bool IsPendingUpdaterProcessRunning()
        {
            if (pendingUpdaterProcessId <= 0)
            {
                return false;
            }

            try
            {
                using (var process = Process.GetProcessById(pendingUpdaterProcessId))
                {
                    return !process.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDeletePendingUpdaterExecutable(string updaterPath)
        {
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    if (!File.Exists(updaterPath))
                    {
                        return true;
                    }

                    File.Delete(updaterPath);
                    return true;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                Thread.Sleep(200);
            }

            return false;
        }

        private bool IsSafePendingUpdateStagingDirectory(string stagingDirectory)
        {
            try
            {
                var fullPath = Path.GetFullPath(stagingDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var updateRoot = Path.GetFullPath(updateDownloadDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var directoryName = Path.GetFileName(fullPath);

                return fullPath.StartsWith(updateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                       directoryName.StartsWith("prepared-", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private bool IsSafePendingUpdaterExecutablePath(string updaterPath)
        {
            try
            {
                var fullPath = Path.GetFullPath(updaterPath);
                var updateRoot = Path.GetFullPath(updateDownloadDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fileName = Path.GetFileName(fullPath);

                return fullPath.StartsWith(updateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                       fileName.StartsWith("FishXIVItemReader.Updater.", StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(Path.GetExtension(fileName), ".exe", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void CloseActForUpdate()
        {
            var form = ActGlobals.oFormActMain;
            if (form == null)
            {
                Application.Exit();
                return;
            }

            Action closeAction = delegate
            {
                if (!form.IsDisposed)
                {
                    form.Close();
                }

                Application.Exit();
            };

            if (form.InvokeRequired)
            {
                form.BeginInvoke(closeAction);
            }
            else
            {
                closeAction();
            }
        }

        private bool BeginUpdateOperation(string statusText)
        {
            if (updateOperationRunning)
            {
                return false;
            }

            updateOperationRunning = true;
            updateCancellation = new CancellationTokenSource();
            updateStatusValueLabel.Text = statusText;
            UpdateUpdateButtons();
            return true;
        }

        private void EndUpdateOperation()
        {
            var cancellation = updateCancellation;
            updateCancellation = null;
            if (cancellation != null)
            {
                cancellation.Dispose();
            }

            updateOperationRunning = false;
            UpdateUpdateButtons();
        }

        private void CancelUpdateOperation()
        {
            var cancellation = updateCancellation;
            if (cancellation != null)
            {
                cancellation.Cancel();
            }
        }

        private void UpdateUpdateButtons()
        {
            checkUpdateButton.Enabled = !updateOperationRunning;
            downloadUpdateButton.Enabled =
                !updateOperationRunning &&
                latestUpdateCheckResult != null &&
                latestUpdateCheckResult.UpdateAvailable;
        }

        private void RestartAutoRead()
        {
            if (!pluginInitialized || suppressAutoRestart)
            {
                return;
            }

            var request = CreateAutoReadRequest();
            var stopNetworkReader = activeAutoReadMode == InventoryReadMode.Network &&
                (request == null ||
                    request.Mode != InventoryReadMode.Network ||
                    request.ProcessId != activeAutoReadProcessId);

            StopAutoRead(stopNetworkReader);

            if (request == null)
            {
                inventoryWebSocketServer.SetMonitoredProcess(0);
                ClearInventoryGrid();
                SetStatusPanel("未运行", "-");
                SetStatus("没有可自动读取的有效 FFXIV 进程。");
                return;
            }

            activeAutoReadProcessId = request.ProcessId;
            activeAutoReadMode = request.Mode;
            inventoryWebSocketServer.SetMonitoredProcess(request.ProcessId);
            lastInventoryGridSignature = null;
            SetNetworkDetailVisibility(ShouldShowNetworkDetailPanel(request.Mode));

            var cancellation = new CancellationTokenSource();
            autoReadCancellation = cancellation;
            var version = ++autoReadVersion;
            lastAutoReadError = null;

            var task = Task.Run(() => AutoReadLoop(request, version, cancellation.Token));
            autoReadTask = task;
            task.ContinueWith(
                completedTask =>
                {
                    var ignored = completedTask.Exception;
                    cancellation.Dispose();
                },
                TaskContinuationOptions.ExecuteSynchronously);

            SetStatusPanel("自动读取中", "等待快照");
            SetStatus(request.Mode == InventoryReadMode.Memory
                ? string.Format("内存模式自动读取已启动：PID {0}", request.ProcessId)
                : string.Format("网络模式自动读取已启动：PID {0}", request.ProcessId));
        }

        private void StopAutoRead(bool stopNetworkReader)
        {
            autoReadVersion++;

            var cancellation = autoReadCancellation;
            autoReadCancellation = null;
            autoReadTask = null;
            if (cancellation != null)
            {
                cancellation.Cancel();
            }

            activeAutoReadProcessId = 0;
            inventoryWebSocketServer.SetMonitoredProcess(0);

            if (stopNetworkReader)
            {
                networkInventoryReader.Stop();
            }
        }

        private AutoReadRequest CreateAutoReadRequest()
        {
            var processInfo = processComboBox.SelectedItem as ClientProcessInfo;
            if (processInfo == null || !IsValidGameProcess(processInfo.ProcessId))
            {
                return null;
            }

            return new AutoReadRequest(
                processInfo.ProcessId,
                GetSelectedReadMode(),
                includeSaddleBagCheckBox.Checked,
                includeRetainerStorageCheckBox.Checked,
                includeArmoryChestCheckBox.Checked,
                includeEquippedItemsCheckBox.Checked);
        }

        private void AutoReadLoop(AutoReadRequest request, int version, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!IsValidGameProcess(request.ProcessId))
                    {
                        PostAutoReadError(version, "所选 FFXIV 进程已退出或不再是 ffxiv_dx11.exe。", true);
                        return;
                    }

                    InventoryReadResult result;
                    lock (autoReadExecutionGate)
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        result = request.Mode == InventoryReadMode.Memory
                            ? ClientInventoryReader.ReadPlayerInventory(
                                request.ProcessId,
                                request.IncludeSaddleBags,
                                request.IncludeRetainerStorage,
                                request.IncludeArmoryChest,
                                request.IncludeEquippedItems)
                            : networkInventoryReader.ReadPlayerInventory(
                                request.ProcessId,
                                request.IncludeSaddleBags,
                                request.IncludeRetainerStorage,
                                request.IncludeArmoryChest,
                                request.IncludeEquippedItems,
                                TimeSpan.Zero);
                    }

                    PostAutoReadResult(version, result);
                    if (WaitForNextAutoRead(request.Mode == InventoryReadMode.Memory ? 1000 : 1000, token))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    var errorMessage = ex.Message;

                    PostAutoReadError(version, errorMessage, false);
                    if (WaitForNextAutoRead(request.Mode == InventoryReadMode.Memory ? 1000 : 3000, token))
                    {
                        return;
                    }
                }
            }
        }

        private static bool WaitForNextAutoRead(int milliseconds, CancellationToken token)
        {
            return token.WaitHandle.WaitOne(milliseconds);
        }

        private void PostAutoReadResult(int version, InventoryReadResult result)
        {
            BeginInvokeIfAvailable(delegate
            {
                if (version != autoReadVersion)
                {
                    return;
                }

                lastAutoReadError = null;
                ShowInventory(result);
            });
        }

        private void PostAutoReadError(int version, string message, bool clearGrid)
        {
            BeginInvokeIfAvailable(delegate
            {
                if (version != autoReadVersion)
                {
                    return;
                }

                var text = "自动读取失败：" + message;
                if (string.Equals(lastAutoReadError, text, StringComparison.Ordinal))
                {
                    return;
                }

                lastAutoReadError = text;
                if (clearGrid)
                {
                    ClearInventoryGrid();
                }

                statusInventoryValueLabel.Text = clearGrid ? "-" : statusInventoryValueLabel.Text;
                if (activeAutoReadMode == InventoryReadMode.Network)
                {
                    TryUpdateNetworkDetailPanel();
                }

                SetStatusState("读取失败", message);
            });
        }

        private void BeginInvokeIfAvailable(Action action)
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
            }
        }

        private static bool IsValidGameProcess(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return !process.HasExited &&
                        string.Equals(process.ProcessName, "ffxiv_dx11", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private void UpdateSwitchWindowButton()
        {
            var processInfo = processComboBox.SelectedItem as ClientProcessInfo;
            switchWindowButton.Enabled = processInfo != null && IsValidGameProcess(processInfo.ProcessId);
        }

        private void SwitchToSelectedGameWindow()
        {
            var processInfo = processComboBox.SelectedItem as ClientProcessInfo;
            if (processInfo == null)
            {
                SetStatusState("未选择进程", "请先选择 FFXIV 进程。");
                return;
            }

            if (!IsValidGameProcess(processInfo.ProcessId))
            {
                UpdateSwitchWindowButton();
                SetStatusState("进程无效", "所选 FFXIV 进程已退出或不再是 ffxiv_dx11.exe。");
                return;
            }

            var windowHandle = FindProcessWindow(processInfo.ProcessId);
            if (windowHandle == IntPtr.Zero)
            {
                SetStatusState("窗口未找到", string.Format("未找到 PID {0} 对应的游戏窗口。", processInfo.ProcessId));
                return;
            }

            ShowWindow(windowHandle, SwRestore);
            if (!SetForegroundWindow(windowHandle))
            {
                SetStatusState("切换窗口失败", string.Format("无法将 PID {0} 的游戏窗口切换到前台。", processInfo.ProcessId));
                return;
            }

            SetStatusState("窗口已切换", string.Format("已切换到 FFXIV 窗口：PID {0}。", processInfo.ProcessId));
        }

        private void ApplyWebSocketPortSetting()
        {
            configuredWebSocketPort = (int)webSocketPortNumericUpDown.Value;
            SaveSettings();
            RestartInventoryWebSocketServer("WebSocket 端口已应用。");
        }

        private void RestartInventoryWebSocketServer(string successDetail)
        {
            inventoryWebSocketServer.Stop();
            RefreshWebSocketStatusPanel();
            inventoryWebSocketServer.SetMonitoredProcess(activeAutoReadProcessId);
            StartInventoryWebSocketServer(successDetail);
        }

        private void StartInventoryWebSocketServer()
        {
            StartInventoryWebSocketServer(null);
        }

        private void StartInventoryWebSocketServer(string successDetail)
        {
            try
            {
                inventoryWebSocketServer.Start(configuredWebSocketPort);
                inventoryWebSocketServer.SetMonitoredProcess(activeAutoReadProcessId);
                overlayPluginEventBridge.TryConnect();
                SetStatusWebSocket(BuildWebSocketStatusText(), successDetail);
                SetStatusOverlayPlugin(overlayPluginEventBridge.StatusText, null);
            }
            catch (Exception ex)
            {
                SetStatusWebSocket("启动失败", "库存 WebSocket 启动失败：" + ex.Message);
                SetStatusOverlayPlugin(overlayPluginEventBridge.StatusText, null);
            }
        }

        private static IntPtr FindProcessWindow(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    process.Refresh();
                    if (!process.HasExited &&
                        process.MainWindowHandle != IntPtr.Zero &&
                        IsWindowVisible(process.MainWindowHandle))
                    {
                        return process.MainWindowHandle;
                    }
                }
            }
            catch
            {
            }

            var foundWindow = IntPtr.Zero;
            EnumWindows(
                delegate(IntPtr windowHandle, IntPtr lParam)
                {
                    int windowProcessId;
                    GetWindowThreadProcessId(windowHandle, out windowProcessId);
                    if (windowProcessId == processId && IsWindowVisible(windowHandle))
                    {
                        foundWindow = windowHandle;
                        return false;
                    }

                    return true;
                },
                IntPtr.Zero);

            return foundWindow;
        }

        private void ShowInventory(InventoryReadResult result)
        {
            inventoryWebSocketServer.Publish(result);
            if (debugModeCheckBox.Checked)
            {
                var gridSignature = BuildInventoryGridSignature(result);
                if (!string.Equals(lastInventoryGridSignature, gridSignature, StringComparison.Ordinal))
                {
                    lastInventoryGridSignature = gridSignature;
                    RebuildInventoryGrid(result);
                }
            }

            SetReadCountStatus(result);
        }

        private void RebuildInventoryGrid(InventoryReadResult result)
        {
            var firstDisplayedRowIndex = GetFirstDisplayedGridRowIndex();
            var currentRowIndex = inventoryGrid.CurrentCell == null ? -1 : inventoryGrid.CurrentCell.RowIndex;
            var currentColumnIndex = inventoryGrid.CurrentCell == null ? -1 : inventoryGrid.CurrentCell.ColumnIndex;

            inventoryGrid.SuspendLayout();
            try
            {
                inventoryGrid.Rows.Clear();

                foreach (var item in result.Items)
                {
                    inventoryGrid.Rows.Add(
                        FormatInventoryType(item.Container),
                        item.Slot,
                        item.ItemId,
                        item.Quantity,
                        item.IsHighQuality ? "是" : string.Empty,
                        item.IsCollectable ? "是" : string.Empty,
                        item.Condition,
                        FormatItemFlags(item.Flags));
                }
            }
            finally
            {
                inventoryGrid.ResumeLayout();
            }

            RestoreGridViewPosition(firstDisplayedRowIndex, currentRowIndex, currentColumnIndex);
        }

        private static string BuildInventoryGridSignature(InventoryReadResult result)
        {
            var sb = new StringBuilder(result.Items.Count * 48);
            foreach (var item in result.Items)
            {
                sb.Append((int)item.Container);
                sb.Append('|');
                sb.Append(item.Slot);
                sb.Append('|');
                sb.Append(item.ItemId);
                sb.Append('|');
                sb.Append(item.Quantity);
                sb.Append('|');
                sb.Append(item.IsHighQuality ? '1' : '0');
                sb.Append('|');
                sb.Append(item.IsCollectable ? '1' : '0');
                sb.Append('|');
                sb.Append(item.Condition);
                sb.Append('|');
                sb.Append((int)item.Flags);
                sb.Append('\n');
            }

            return sb.ToString();
        }

        private int GetFirstDisplayedGridRowIndex()
        {
            try
            {
                return inventoryGrid.FirstDisplayedScrollingRowIndex;
            }
            catch
            {
                return -1;
            }
        }

        private void RestoreGridViewPosition(int firstDisplayedRowIndex, int currentRowIndex, int currentColumnIndex)
        {
            if (inventoryGrid.Rows.Count == 0)
            {
                return;
            }

            if (currentRowIndex >= 0 && currentRowIndex < inventoryGrid.Rows.Count)
            {
                var columnIndex = currentColumnIndex >= 0 && currentColumnIndex < inventoryGrid.Columns.Count
                    ? currentColumnIndex
                    : 0;
                try
                {
                    inventoryGrid.CurrentCell = inventoryGrid.Rows[currentRowIndex].Cells[columnIndex];
                }
                catch
                {
                }
            }

            if (firstDisplayedRowIndex >= 0 && firstDisplayedRowIndex < inventoryGrid.Rows.Count)
            {
                try
                {
                    inventoryGrid.FirstDisplayedScrollingRowIndex = firstDisplayedRowIndex;
                }
                catch
                {
                }
            }
        }

        private void ClearInventoryGrid()
        {
            lastInventoryGridSignature = null;
            inventoryGrid.Rows.Clear();
        }

        private void UpdateDebugGridVisibility()
        {
            inventoryGrid.Visible = debugModeCheckBox.Checked;
            lastInventoryGridSignature = null;
            if (!debugModeCheckBox.Checked)
            {
                inventoryGrid.Rows.Clear();
            }

            SetNetworkDetailVisibility(ShouldShowNetworkDetailPanel());
            if (ShouldShowNetworkDetailPanel())
            {
                TryUpdateNetworkDetailPanel();
            }
        }

        private void SetReadCountStatus(InventoryReadResult result)
        {
            statusInventoryValueLabel.Text = string.Format(
                "{0} 组 / {1} 容器 / {2} 槽",
                result.Items.Count,
                result.ContainersRead,
                result.SlotsRead);

            if (result.ReadMode == InventoryReadMode.Network)
            {
                TryUpdateNetworkDetailPanel();
                var networkText = result.Items.Count == 0
                    ? "网络模式尚未形成背包快照。"
                    : string.Format("网络模式已读取到 {0} 组物品。", result.Items.Count);
                statusStateValueLabel.Text = result.Items.Count == 0 ? "等待快照" : "已读取";
                SetStatus(networkText);
                return;
            }

            var text = string.Format("内存模式已读取到 {0} 组物品。", result.Items.Count);
            statusStateValueLabel.Text = "已读取";
            SetStatus(text);
        }

        private static string FormatInventoryType(ClientInventoryType type)
        {
            switch (type)
            {
                case ClientInventoryType.Inventory1:
                    return "背包第 1 页";
                case ClientInventoryType.Inventory2:
                    return "背包第 2 页";
                case ClientInventoryType.Inventory3:
                    return "背包第 3 页";
                case ClientInventoryType.Inventory4:
                    return "背包第 4 页";
                case ClientInventoryType.EquippedItems:
                    return "当前装备";
                case ClientInventoryType.Currency:
                    return "货币";
                case ClientInventoryType.Crystals:
                    return "水晶";
                case ClientInventoryType.KeyItems:
                    return "重要物品";
                case ClientInventoryType.ArmoryMainHand:
                    return "兵装库：主手";
                case ClientInventoryType.ArmoryOffHand:
                    return "兵装库：副手";
                case ClientInventoryType.ArmoryHead:
                    return "兵装库：头部";
                case ClientInventoryType.ArmoryBody:
                    return "兵装库：身体";
                case ClientInventoryType.ArmoryHands:
                    return "兵装库：手部";
                case ClientInventoryType.ArmoryWaist:
                    return "兵装库：腰部";
                case ClientInventoryType.ArmoryLegs:
                    return "兵装库：腿部";
                case ClientInventoryType.ArmoryFeets:
                    return "兵装库：脚部";
                case ClientInventoryType.ArmoryEar:
                    return "兵装库：耳饰";
                case ClientInventoryType.ArmoryNeck:
                    return "兵装库：项链";
                case ClientInventoryType.ArmoryWrist:
                    return "兵装库：手镯";
                case ClientInventoryType.ArmoryRings:
                    return "兵装库：戒指";
                case ClientInventoryType.ArmorySoulCrystal:
                    return "兵装库：灵魂水晶";
                case ClientInventoryType.SaddleBag1:
                    return "陆行鸟鞍囊 1";
                case ClientInventoryType.SaddleBag2:
                    return "陆行鸟鞍囊 2";
                case ClientInventoryType.PremiumSaddleBag1:
                    return "额外鞍囊 1";
                case ClientInventoryType.PremiumSaddleBag2:
                    return "额外鞍囊 2";
                case ClientInventoryType.RetainerPage1:
                    return "雇员仓库第 1 页";
                case ClientInventoryType.RetainerPage2:
                    return "雇员仓库第 2 页";
                case ClientInventoryType.RetainerPage3:
                    return "雇员仓库第 3 页";
                case ClientInventoryType.RetainerPage4:
                    return "雇员仓库第 4 页";
                case ClientInventoryType.RetainerPage5:
                    return "雇员仓库第 5 页";
                case ClientInventoryType.RetainerPage6:
                    return "雇员仓库第 6 页";
                case ClientInventoryType.RetainerPage7:
                    return "雇员仓库第 7 页";
                case ClientInventoryType.Invalid:
                    return "无效";
                default:
                    return string.Format("未知({0})", (uint)type);
            }
        }

        private static string FormatItemFlags(ClientItemFlags flags)
        {
            if (flags == ClientItemFlags.None)
                return string.Empty;

            var parts = new System.Collections.Generic.List<string>();
            if ((flags & ClientItemFlags.HighQuality) != 0)
                parts.Add("高品质");
            if ((flags & ClientItemFlags.CompanyCrestApplied) != 0)
                parts.Add("部队纹章");
            if ((flags & ClientItemFlags.Relic) != 0)
                parts.Add("古武");
            if ((flags & ClientItemFlags.Collectable) != 0)
                parts.Add("收藏品");

            var knownFlags = ClientItemFlags.HighQuality |
                ClientItemFlags.CompanyCrestApplied |
                ClientItemFlags.Relic |
                ClientItemFlags.Collectable;
            var unknownFlags = flags & ~knownFlags;
            if (unknownFlags != 0)
                parts.Add(string.Format("未知标记({0})", (byte)unknownFlags));

            return string.Join("，", parts);
        }

        private void SetStatus(string text)
        {
            if (statusLabel != null)
                statusLabel.Text = text;
        }

        private static string ResolvePluginDirectory()
        {
            var location = typeof(PluginMain).Assembly.Location;
            if (string.IsNullOrWhiteSpace(location))
            {
                return string.Empty;
            }

            return Path.GetDirectoryName(location) ?? string.Empty;
        }

        private void LoadSettings()
        {
            if (!File.Exists(settingsFile))
                return;

            using (var stream = new FileStream(settingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new XmlTextReader(stream))
            {
                try
                {
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "SettingsSerializer")
                        {
                            settings.ImportFromXml(reader);
                        }
                        else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "ReadMode")
                        {
                            SetSelectedReadMode(reader.ReadElementContentAsString());
                        }
                        else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "DebugMode")
                        {
                            bool debugMode;
                            if (bool.TryParse(reader.ReadElementContentAsString(), out debugMode))
                            {
                                debugModeCheckBox.Checked = debugMode;
                                UpdateDebugGridVisibility();
                            }
                        }
                        else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "WebSocketPort")
                        {
                            int port;
                            if (int.TryParse(reader.ReadElementContentAsString(), out port))
                            {
                                SetConfiguredWebSocketPort(port);
                            }
                        }
                        else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "PendingUpdaterPath")
                        {
                            pendingUpdaterExecutablePath = reader.ReadElementContentAsString();
                        }
                        else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "PendingUpdateStagingDirectory")
                        {
                            pendingUpdateStagingDirectory = reader.ReadElementContentAsString();
                        }
                    }
                }
                catch (Exception ex)
                {
                    SetStatus("加载设置失败：" + ex.Message);
                }
            }
        }

        private void SaveSettings()
        {
            if (settings == null)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(settingsFile));

            using (var stream = new FileStream(settingsFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new XmlTextWriter(stream, Encoding.UTF8))
            {
                writer.Formatting = Formatting.Indented;
                writer.Indentation = 1;
                writer.IndentChar = '\t';
                writer.WriteStartDocument(true);
                writer.WriteStartElement("Config");
                writer.WriteStartElement("SettingsSerializer");
                settings.ExportToXml(writer);
                writer.WriteEndElement();
                writer.WriteElementString("ReadMode", GetSelectedReadMode().ToString());
                writer.WriteElementString("DebugMode", debugModeCheckBox.Checked ? "true" : "false");
                writer.WriteElementString("WebSocketPort", configuredWebSocketPort.ToString());
                writer.WriteElementString("PendingUpdaterPath", pendingUpdaterExecutablePath ?? string.Empty);
                writer.WriteElementString("PendingUpdateStagingDirectory", pendingUpdateStagingDirectory ?? string.Empty);
                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
        }

        private void SetConfiguredWebSocketPort(int port)
        {
            configuredWebSocketPort = NormalizeWebSocketPort(port);
            webSocketPortNumericUpDown.Value = configuredWebSocketPort;
        }

        private static int NormalizeWebSocketPort(int port)
        {
            return port >= 1 && port <= 65535
                ? port
                : InventoryWebSocketServer.DefaultPort;
        }

        private InventoryReadMode GetSelectedReadMode()
        {
            var option = readModeComboBox.SelectedItem as ReadModeOption;
            return option != null ? option.Mode : InventoryReadMode.Memory;
        }

        private void SetSelectedReadMode(string modeName)
        {
            InventoryReadMode mode;
            if (!Enum.TryParse(modeName, out mode))
            {
                mode = InventoryReadMode.Memory;
            }

            for (var i = 0; i < readModeComboBox.Items.Count; i++)
            {
                var option = readModeComboBox.Items[i] as ReadModeOption;
                if (option != null && option.Mode == mode)
                {
                    readModeComboBox.SelectedIndex = i;
                    return;
                }
            }
        }

        private void UpdateModeHint()
        {
            if (networkDetailGroupBox != null)
            {
                SetNetworkDetailVisibility(ShouldShowNetworkDetailPanel());
                if (ShouldShowNetworkDetailPanel())
                {
                    TryUpdateNetworkDetailPanel();
                }
            }
        }

        private sealed class AutoReadRequest
        {
            public AutoReadRequest(
                int processId,
                InventoryReadMode mode,
                bool includeSaddleBags,
                bool includeRetainerStorage,
                bool includeArmoryChest,
                bool includeEquippedItems)
            {
                ProcessId = processId;
                Mode = mode;
                IncludeSaddleBags = includeSaddleBags;
                IncludeRetainerStorage = includeRetainerStorage;
                IncludeArmoryChest = includeArmoryChest;
                IncludeEquippedItems = includeEquippedItems;
            }

            public int ProcessId { get; }

            public InventoryReadMode Mode { get; }

            public bool IncludeSaddleBags { get; }

            public bool IncludeRetainerStorage { get; }

            public bool IncludeArmoryChest { get; }

            public bool IncludeEquippedItems { get; }
        }

        private sealed class ReadModeOption
        {
            public ReadModeOption(InventoryReadMode mode, string displayName)
            {
                Mode = mode;
                DisplayName = displayName;
            }

            public InventoryReadMode Mode { get; }

            private string DisplayName { get; }

            public override string ToString()
            {
                return DisplayName;
            }
        }

        private const int SwRestore = 9;

        private delegate bool EnumWindowsProc(IntPtr windowHandle, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr windowHandle, out int processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr windowHandle, int command);
    }
}
