using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using FishXIVItemReader.GameClient;

namespace FishXIVItemReader.Web
{
    public sealed class InventoryWebSocketServer : IDisposable
    {
        public const int DefaultPort = 17814;
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaxHandshakeBytes = 16 * 1024;
        private const int MaxIncomingPayloadBytes = 1024 * 1024;
        private const int HeartbeatIntervalMilliseconds = 1000;

        // WebSocket 包 cmdType 取值：
        // 1 = Ping：每秒发送一次的心跳包，携带当前监视的 FFXIV 进程 PID。
        // 2 = InventorySnapshot：背包快照包，仅在背包数据发生变化时发送。
        private const int CmdTypePing = 1;
        private const int CmdTypeInventorySnapshot = 2;

        private readonly object gate = new object();
        private readonly List<ClientConnection> clients = new List<ClientConnection>();
        private TcpListener listener;
        private Thread listenerThread;
        private Timer heartbeatTimer;
        private string latestSnapshotJson;
        private bool hasSnapshotJson;
        private int heartbeatSendInProgress;
        private int monitoredProcessId;
        private int port;
        private string accessToken = string.Empty;
        private volatile bool stopRequested;

        public InventoryWebSocketServer()
        {
            latestSnapshotJson = CreateEmptySnapshotJson();
        }

        public string Url
        {
            get
            {
                lock (gate)
                {
                    return port > 0
                        ? string.Format(CultureInfo.InvariantCulture, "ws://127.0.0.1:{0}/inventory", port)
                        : string.Empty;
                }
            }
        }

        public int ClientCount
        {
            get
            {
                lock (gate)
                {
                    return clients.Count;
                }
            }
        }

        public event Action<string> MessagePublished;

        public event Action ClientCountChanged;

        public bool IsRunning
        {
            get
            {
                lock (gate)
                {
                    return listener != null && port > 0;
                }
            }
        }

        public void Start()
        {
            Start(DefaultPort);
        }

        public void Start(int listenPort)
        {
            lock (gate)
            {
                if (listener != null)
                {
                    return;
                }
            }

            if (listenPort < IPEndPoint.MinPort || listenPort > IPEndPoint.MaxPort)
            {
                throw new ArgumentOutOfRangeException("listenPort", "WebSocket 端口必须在 0-65535 范围内。");
            }

            TcpListener candidate = null;
            try
            {
                candidate = new TcpListener(IPAddress.Loopback, listenPort);
                candidate.Start(16);

                lock (gate)
                {
                    if (listener != null)
                    {
                        candidate.Stop();
                        return;
                    }

                    listener = candidate;
                    listenerThread = new Thread(ListenLoop)
                    {
                        IsBackground = true,
                        Name = "FishXIVItemReader.InventoryWebSocketServer"
                    };
                    port = listenPort;
                    stopRequested = false;
                    heartbeatTimer = new Timer(SendHeartbeat, null, HeartbeatIntervalMilliseconds, HeartbeatIntervalMilliseconds);
                    listenerThread.Start();
                    candidate = null;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    string.Format(CultureInfo.InvariantCulture, "无法在端口 {0} 启动库存 WebSocket 服务。", listenPort),
                    ex);
            }
            finally
            {
                if (candidate != null)
                {
                    try
                    {
                        candidate.Stop();
                    }
                    catch
                    {
                    }
                }
            }
        }

        public void Publish(InventoryReadResult result)
        {
            if (result == null)
            {
                return;
            }

            SetMonitoredProcess(result.ProcessId);
            PublishJson(BuildSnapshotJson(result));
        }

        public void SetMonitoredProcess(int processId)
        {
            lock (gate)
            {
                monitoredProcessId = processId > 0 ? processId : 0;
            }
        }

        public void SetAccessToken(string token)
        {
            lock (gate)
            {
                accessToken = token == null ? string.Empty : token.Trim();
            }
        }

        public void PublishJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            ClientConnection[] connectedClients;
            lock (gate)
            {
                if (hasSnapshotJson && string.Equals(latestSnapshotJson, json, StringComparison.Ordinal))
                {
                    return;
                }

                latestSnapshotJson = json;
                hasSnapshotJson = true;
                connectedClients = clients.ToArray();
            }

            MessagePublished?.Invoke(json);

            foreach (var client in connectedClients)
            {
                if (!TrySendText(client, json))
                {
                    RemoveClient(client);
                }
            }
        }

        public void Stop()
        {
            TcpListener listenerToStop;
            Thread threadToJoin;
            Timer timerToDispose;
            ClientConnection[] clientsToClose;
            bool clientCountChanged;

            lock (gate)
            {
                stopRequested = true;
                listenerToStop = listener;
                threadToJoin = listenerThread;
                timerToDispose = heartbeatTimer;
                clientsToClose = clients.ToArray();
                clientCountChanged = clients.Count > 0;
                clients.Clear();
                listener = null;
                listenerThread = null;
                heartbeatTimer = null;
                port = 0;
            }

            if (timerToDispose != null)
            {
                timerToDispose.Dispose();
            }

            if (listenerToStop != null)
            {
                try
                {
                    listenerToStop.Stop();
                }
                catch
                {
                }
            }

            foreach (var client in clientsToClose)
            {
                CloseClient(client);
            }

            if (threadToJoin != null && threadToJoin.IsAlive)
            {
                threadToJoin.Join(500);
            }

            if (clientCountChanged)
            {
                OnClientCountChanged();
            }
        }

        public void Dispose()
        {
            Stop();
        }

        private void ListenLoop()
        {
            while (!stopRequested)
            {
                TcpListener activeListener;
                lock (gate)
                {
                    activeListener = listener;
                }

                if (activeListener == null)
                {
                    return;
                }

                try
                {
                    var tcpClient = activeListener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, tcpClient);
                }
                catch (ObjectDisposedException)
                {
                    if (stopRequested)
                    {
                        return;
                    }
                }
                catch (SocketException)
                {
                    if (stopRequested)
                    {
                        return;
                    }
                }
                catch
                {
                }
            }
        }

        private void HandleClient(object state)
        {
            var tcpClient = state as TcpClient;
            if (tcpClient == null)
            {
                return;
            }

            ClientConnection connection = null;
            try
            {
                tcpClient.NoDelay = true;
                tcpClient.ReceiveTimeout = 15000;
                tcpClient.SendTimeout = 15000;

                var stream = tcpClient.GetStream();
                string path;
                string requestToken;
                var headers = ReadHandshakeHeaders(stream, out path, out requestToken);
                if (headers == null)
                {
                    tcpClient.Close();
                    return;
                }

                if (!string.Equals(path, "/inventory", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(path, "/", StringComparison.OrdinalIgnoreCase))
                {
                    WriteHttpError(stream, "404 Not Found");
                    tcpClient.Close();
                    return;
                }

                if (!IsAccessTokenAccepted(requestToken))
                {
                    WriteHttpError(stream, "401 Unauthorized");
                    tcpClient.Close();
                    return;
                }

                string key;
                if (!headers.TryGetValue("sec-websocket-key", out key) ||
                    !IsWebSocketUpgrade(headers))
                {
                    WriteHttpError(stream, "400 Bad Request");
                    tcpClient.Close();
                    return;
                }

                WriteHandshakeResponse(stream, key);
                tcpClient.ReceiveTimeout = 0;

                connection = new ClientConnection(tcpClient, stream);
                lock (gate)
                {
                    clients.Add(connection);
                }
                OnClientCountChanged();

                string snapshot;
                bool sendSnapshot;
                lock (gate)
                {
                    snapshot = latestSnapshotJson;
                    sendSnapshot = hasSnapshotJson;
                }

                if (sendSnapshot && !TrySendText(connection, snapshot))
                {
                    return;
                }

                ReceiveLoop(connection);
            }
            catch
            {
            }
            finally
            {
                if (connection != null)
                {
                    RemoveClient(connection);
                }
                else
                {
                    try
                    {
                        tcpClient.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void SendHeartbeat(object state)
        {
            if (stopRequested)
            {
                return;
            }

            if (Interlocked.Exchange(ref heartbeatSendInProgress, 1) == 1)
            {
                return;
            }

            try
            {
                ClientConnection[] connectedClients;
                string pingJson;
                lock (gate)
                {
                    if (listener == null || stopRequested)
                    {
                        return;
                    }

                    connectedClients = clients.ToArray();
                    pingJson = CreatePingJson(monitoredProcessId);
                }

                MessagePublished?.Invoke(pingJson);

                foreach (var client in connectedClients)
                {
                    if (!TrySendText(client, pingJson))
                    {
                        RemoveClient(client);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref heartbeatSendInProgress, 0);
            }
        }

        private static Dictionary<string, string> ReadHandshakeHeaders(NetworkStream stream, out string path, out string accessToken)
        {
            path = "/";
            accessToken = string.Empty;
            var buffer = new List<byte>(1024);
            var lastBytes = new Queue<byte>(4);

            while (buffer.Count < MaxHandshakeBytes)
            {
                var next = stream.ReadByte();
                if (next < 0)
                {
                    return null;
                }

                var value = (byte)next;
                buffer.Add(value);
                lastBytes.Enqueue(value);
                while (lastBytes.Count > 4)
                {
                    lastBytes.Dequeue();
                }

                if (lastBytes.Count == 4)
                {
                    var check = lastBytes.ToArray();
                    if (check[0] == (byte)'\r' &&
                        check[1] == (byte)'\n' &&
                        check[2] == (byte)'\r' &&
                        check[3] == (byte)'\n')
                    {
                        break;
                    }
                }
            }

            var headerText = Encoding.ASCII.GetString(buffer.ToArray());
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return null;
            }

            var requestParts = lines[0].Split(' ');
            if (requestParts.Length < 2 || !string.Equals(requestParts[0], "GET", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var target = requestParts[1];
            var queryIndex = target.IndexOf('?');
            path = queryIndex >= 0 ? target.Substring(0, queryIndex) : target;
            accessToken = ExtractQueryValue(target, "token");

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                headers[line.Substring(0, separator).Trim().ToLowerInvariant()] =
                    line.Substring(separator + 1).Trim();
            }

            return headers;
        }

        private bool IsAccessTokenAccepted(string token)
        {
            string expectedToken;
            lock (gate)
            {
                expectedToken = accessToken;
            }

            if (string.IsNullOrWhiteSpace(expectedToken) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var expectedBytes = Encoding.UTF8.GetBytes(expectedToken.Trim());
            var actualBytes = Encoding.UTF8.GetBytes(token.Trim());
            var diff = expectedBytes.Length ^ actualBytes.Length;
            var count = Math.Max(expectedBytes.Length, actualBytes.Length);
            for (var i = 0; i < count; i++)
            {
                var expected = i < expectedBytes.Length ? expectedBytes[i] : (byte)0;
                var actual = i < actualBytes.Length ? actualBytes[i] : (byte)0;
                diff |= expected ^ actual;
            }

            return diff == 0;
        }

        private static string ExtractQueryValue(string target, string name)
        {
            var queryIndex = target.IndexOf('?');
            if (queryIndex < 0 || queryIndex + 1 >= target.Length)
            {
                return string.Empty;
            }

            var query = target.Substring(queryIndex + 1);
            var pairs = query.Split('&');
            foreach (var pair in pairs)
            {
                if (string.IsNullOrEmpty(pair))
                {
                    continue;
                }

                var separator = pair.IndexOf('=');
                var rawName = separator >= 0 ? pair.Substring(0, separator) : pair;
                if (!string.Equals(UrlDecode(rawName), name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rawValue = separator >= 0 ? pair.Substring(separator + 1) : string.Empty;
                return UrlDecode(rawValue);
            }

            return string.Empty;
        }

        private static string UrlDecode(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return Uri.UnescapeDataString(value.Replace("+", " "));
        }

        private static bool IsWebSocketUpgrade(Dictionary<string, string> headers)
        {
            string upgrade;
            string connection;
            return headers.TryGetValue("upgrade", out upgrade) &&
                headers.TryGetValue("connection", out connection) &&
                upgrade.IndexOf("websocket", StringComparison.OrdinalIgnoreCase) >= 0 &&
                connection.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void WriteHandshakeResponse(Stream stream, string key)
        {
            var accept = ComputeAcceptKey(key);
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n" +
                "\r\n");
            stream.Write(response, 0, response.Length);
        }

        private static void WriteHttpError(Stream stream, string status)
        {
            var body = Encoding.UTF8.GetBytes(status);
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + status + "\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Connection: close\r\n" +
                "\r\n");
            stream.Write(response, 0, response.Length);
            stream.Write(body, 0, body.Length);
        }

        private static string ComputeAcceptKey(string key)
        {
            using (var sha1 = SHA1.Create())
            {
                var bytes = Encoding.ASCII.GetBytes((key ?? string.Empty).Trim() + WebSocketGuid);
                return Convert.ToBase64String(sha1.ComputeHash(bytes));
            }
        }

        private void ReceiveLoop(ClientConnection connection)
        {
            while (!stopRequested && connection.IsConnected)
            {
                WebSocketFrame frame;
                if (!TryReadFrame(connection.Stream, out frame))
                {
                    return;
                }

                if (frame.Opcode == 0x8)
                {
                    TrySendFrame(connection, 0x8, new byte[0]);
                    return;
                }

                if (frame.Opcode == 0x9)
                {
                    TrySendFrame(connection, 0xA, frame.Payload);
                }
            }
        }

        private static bool TryReadFrame(NetworkStream stream, out WebSocketFrame frame)
        {
            frame = null;

            try
            {
                var first = stream.ReadByte();
                var second = stream.ReadByte();
                if (first < 0 || second < 0)
                {
                    return false;
                }

                var opcode = first & 0x0F;
                var masked = (second & 0x80) != 0;
                ulong length = (ulong)(second & 0x7F);

                if (length == 126)
                {
                    var extended = ReadExact(stream, 2);
                    if (extended == null)
                    {
                        return false;
                    }

                    length = ((ulong)extended[0] << 8) | extended[1];
                }
                else if (length == 127)
                {
                    var extended = ReadExact(stream, 8);
                    if (extended == null)
                    {
                        return false;
                    }

                    length = 0;
                    for (var i = 0; i < extended.Length; i++)
                    {
                        length = (length << 8) | extended[i];
                    }
                }

                if (length > MaxIncomingPayloadBytes)
                {
                    return false;
                }

                byte[] mask = null;
                if (masked)
                {
                    mask = ReadExact(stream, 4);
                    if (mask == null)
                    {
                        return false;
                    }
                }

                var payload = ReadExact(stream, (int)length);
                if (payload == null)
                {
                    return false;
                }

                if (masked)
                {
                    for (var i = 0; i < payload.Length; i++)
                    {
                        payload[i] = (byte)(payload[i] ^ mask[i % 4]);
                    }
                }

                frame = new WebSocketFrame(opcode, payload);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] ReadExact(Stream stream, int length)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(buffer, offset, length - offset);
                if (read <= 0)
                {
                    return null;
                }

                offset += read;
            }

            return buffer;
        }

        private static bool TrySendText(ClientConnection connection, string text)
        {
            return TrySendFrame(connection, 0x1, Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        private static bool TrySendFrame(ClientConnection connection, int opcode, byte[] payload)
        {
            try
            {
                var frame = BuildFrame(opcode, payload ?? new byte[0]);
                lock (connection.SendGate)
                {
                    if (!connection.IsConnected)
                    {
                        return false;
                    }

                    connection.Stream.Write(frame, 0, frame.Length);
                    connection.Stream.Flush();
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static byte[] BuildFrame(int opcode, byte[] payload)
        {
            using (var frame = new MemoryStream())
            {
                frame.WriteByte((byte)(0x80 | (opcode & 0x0F)));
                if (payload.Length <= 125)
                {
                    frame.WriteByte((byte)payload.Length);
                }
                else if (payload.Length <= 65535)
                {
                    frame.WriteByte(126);
                    frame.WriteByte((byte)((payload.Length >> 8) & 0xFF));
                    frame.WriteByte((byte)(payload.Length & 0xFF));
                }
                else
                {
                    frame.WriteByte(127);
                    var length = (ulong)payload.Length;
                    for (var i = 7; i >= 0; i--)
                    {
                        frame.WriteByte((byte)((length >> (8 * i)) & 0xFF));
                    }
                }

                frame.Write(payload, 0, payload.Length);
                return frame.ToArray();
            }
        }

        private void RemoveClient(ClientConnection connection)
        {
            bool removed;
            lock (gate)
            {
                removed = clients.Remove(connection);
            }

            CloseClient(connection);
            if (removed)
            {
                OnClientCountChanged();
            }
        }

        private void OnClientCountChanged()
        {
            var handler = ClientCountChanged;
            if (handler != null)
            {
                handler();
            }
        }

        private static void CloseClient(ClientConnection connection)
        {
            if (connection == null)
            {
                return;
            }

            connection.IsConnected = false;
            try
            {
                connection.TcpClient.Close();
            }
            catch
            {
            }
        }

        private static string BuildSnapshotJson(InventoryReadResult result)
        {
            var items = result.Items
                .OrderBy(item => (uint)item.Container)
                .ThenBy(item => item.Slot)
                .ToList();
            var containerIds = items
                .Select(item => (uint)item.Container)
                .Distinct()
                .OrderBy(container => container)
                .ToList();

            var sb = new StringBuilder(8192);
            sb.Append('{');
            AppendNumberProperty(sb, "cmdType", CmdTypeInventorySnapshot, true);
            AppendNumberProperty(sb, "version", 1, false);
            AppendNumberProperty(sb, "processId", result.ProcessId, false);

            sb.Append(",\"containers\":[");
            for (var i = 0; i < containerIds.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                var containerId = containerIds[i];
                var itemCount = items.Count(item => (uint)item.Container == containerId);
                sb.Append('{');
                AppendNumberProperty(sb, "id", containerId, true);
                AppendStringProperty(sb, "label", ContainerLabel(containerId), false);
                AppendNumberProperty(sb, "slots", GuessSlotsForContainer(containerId, items), false);
                AppendNumberProperty(sb, "itemCount", itemCount, false);
                sb.Append('}');
            }

            sb.Append(']');

            sb.Append(",\"items\":[");
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                var item = items[i];
                var containerId = (uint)item.Container;
                sb.Append('{');
                AppendNumberProperty(sb, "containerId", containerId, true);
                AppendStringProperty(sb, "containerLabel", ContainerLabel(containerId), false);
                AppendNumberProperty(sb, "slotIndex", item.Slot, false);
                AppendNumberProperty(sb, "itemId", item.ItemId, false);
                AppendStringProperty(sb, "itemName", "物品 " + item.ItemId.ToString(CultureInfo.InvariantCulture), false);
                AppendNumberProperty(sb, "quantity", item.Quantity, false);
                AppendNumberProperty(sb, "condition", item.Condition, false);
                AppendNumberProperty(sb, "spiritbondOrCollectability", item.SpiritbondOrCollectability, false);
                AppendNumberProperty(sb, "glamourItemId", item.GlamourId, false);
                AppendNumberProperty(sb, "flags", (int)item.Flags, false);
                AppendStringProperty(sb, "flagsText", FormatItemFlags(item.Flags), false);
                AppendBoolProperty(sb, "highQuality", item.IsHighQuality, false);
                AppendBoolProperty(sb, "collectable", item.IsCollectable, false);
                AppendStringProperty(sb, "address", item.Address == 0 ? string.Empty : string.Format(CultureInfo.InvariantCulture, "0x{0:X}", item.Address), false);
                sb.Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static string CreateEmptySnapshotJson()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            AppendNumberProperty(sb, "cmdType", CmdTypeInventorySnapshot, true);
            AppendNumberProperty(sb, "version", 1, false);
            AppendNumberProperty(sb, "processId", 0, false);
            sb.Append(",\"containers\":[],\"items\":[]}");
            return sb.ToString();
        }

        private static string CreatePingJson(int processId)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{{\"cmdType\":{0},\"processId\":{1}}}",
                CmdTypePing,
                processId > 0 ? processId : 0);
        }

        private static void AppendStringProperty(StringBuilder sb, string name, string value, bool first)
        {
            AppendPropertyPrefix(sb, name, first);
            sb.Append('"');
            sb.Append(JsonEscape(value ?? string.Empty));
            sb.Append('"');
        }

        private static void AppendNumberProperty(StringBuilder sb, string name, int value, bool first)
        {
            AppendPropertyPrefix(sb, name, first);
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendNumberProperty(StringBuilder sb, string name, uint value, bool first)
        {
            AppendPropertyPrefix(sb, name, first);
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendNumberProperty(StringBuilder sb, string name, ushort value, bool first)
        {
            AppendPropertyPrefix(sb, name, first);
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendBoolProperty(StringBuilder sb, string name, bool value, bool first)
        {
            AppendPropertyPrefix(sb, name, first);
            sb.Append(value ? "true" : "false");
        }

        private static void AppendPropertyPrefix(StringBuilder sb, string name, bool first)
        {
            if (!first)
            {
                sb.Append(',');
            }

            sb.Append('"');
            sb.Append(name);
            sb.Append("\":");
        }

        private static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(value.Length + 8);
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (ch < ' ')
                        {
                            sb.Append("\\u");
                            sb.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(ch);
                        }

                        break;
                }
            }

            return sb.ToString();
        }

        private static int GuessSlotsForContainer(uint containerId, IReadOnlyList<InventoryItemSnapshot> items)
        {
            if (containerId <= 3 ||
                containerId == 4000 ||
                containerId == 4001 ||
                containerId == 4100 ||
                containerId == 4101 ||
                (containerId >= 10000 && containerId <= 10006))
            {
                return 35;
            }

            if (containerId == 1000)
            {
                return 14;
            }

            var highestSlot = items
                .Where(item => (uint)item.Container == containerId)
                .Select(item => item.Slot)
                .DefaultIfEmpty(-1)
                .Max();
            return Math.Min(70, Math.Max(14, highestSlot + 1));
        }

        private static string ContainerLabel(uint containerId)
        {
            switch (containerId)
            {
                case 0:
                    return "背包 1";
                case 1:
                    return "背包 2";
                case 2:
                    return "背包 3";
                case 3:
                    return "背包 4";
                case 1000:
                    return "当前装备";
                case 2000:
                    return "货币";
                case 2001:
                    return "水晶";
                case 2004:
                    return "重要物品";
                case 3200:
                    return "副手库";
                case 3201:
                    return "头部库";
                case 3202:
                    return "身体库";
                case 3203:
                    return "手部库";
                case 3204:
                    return "腰部库";
                case 3205:
                    return "腿部库";
                case 3206:
                    return "脚部库";
                case 3207:
                    return "耳饰库";
                case 3208:
                    return "项链库";
                case 3209:
                    return "腕饰库";
                case 3300:
                    return "戒指库";
                case 3400:
                    return "灵魂水晶";
                case 3500:
                    return "武器库";
                case 4000:
                    return "陆行鸟鞍囊 1";
                case 4001:
                    return "陆行鸟鞍囊 2";
                case 4100:
                    return "额外鞍囊 1";
                case 4101:
                    return "额外鞍囊 2";
                case 10000:
                    return "雇员仓库 1";
                case 10001:
                    return "雇员仓库 2";
                case 10002:
                    return "雇员仓库 3";
                case 10003:
                    return "雇员仓库 4";
                case 10004:
                    return "雇员仓库 5";
                case 10005:
                    return "雇员仓库 6";
                case 10006:
                    return "雇员仓库 7";
                default:
                    return containerId.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string FormatItemFlags(ClientItemFlags flags)
        {
            if (flags == ClientItemFlags.None)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if ((flags & ClientItemFlags.HighQuality) != 0)
            {
                parts.Add("高品质");
            }

            if ((flags & ClientItemFlags.CompanyCrestApplied) != 0)
            {
                parts.Add("部队纹章");
            }

            if ((flags & ClientItemFlags.Relic) != 0)
            {
                parts.Add("古武");
            }

            if ((flags & ClientItemFlags.Collectable) != 0)
            {
                parts.Add("收藏品");
            }

            return string.Join("，", parts.ToArray());
        }

        private sealed class ClientConnection
        {
            public ClientConnection(TcpClient tcpClient, NetworkStream stream)
            {
                TcpClient = tcpClient;
                Stream = stream;
                SendGate = new object();
                IsConnected = true;
            }

            public TcpClient TcpClient { get; private set; }

            public NetworkStream Stream { get; private set; }

            public object SendGate { get; private set; }

            public bool IsConnected { get; set; }
        }

        private sealed class WebSocketFrame
        {
            public WebSocketFrame(int opcode, byte[] payload)
            {
                Opcode = opcode;
                Payload = payload ?? new byte[0];
            }

            public int Opcode { get; private set; }

            public byte[] Payload { get; private set; }
        }
    }
}
