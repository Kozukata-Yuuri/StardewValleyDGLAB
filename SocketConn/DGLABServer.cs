#region License
/* The MIT License
 *
 * Copyright (c) 2025 yl_lty
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */
#endregion
using StardewValley;
using StardewValley.Menus;
using System;
using System.Text.Json;
using System.Threading;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace StardewValleyDGLAB.SocketConn
{
    /// <summary>
    /// V4 协议服务器。本类同时充当两个角色:
    /// 1. 中继服务器 —— 官方 V4 架构里 App 和控制方都连接到同一个中继服务器,
    ///    这里把中继服务器直接嵌进模组,App 扫码后通过局域网直连,不经过官方服务器;
    /// 2. 控制方 —— 直接在本类内部给 App 下发 RPC 指令(device.op / device.op.clear)。
    /// 协议参考: https://github.com/dungeonlab-open/dglab-kit (V4 协议参考章节)
    /// </summary>
    public class DGLABServer : WebSocketBehavior
    {
        #region 服务器
        public static WebSocketServer server;
        /// <summary> 当前接入的 App(被控方)连接 </summary>
        public static DGLABServer _currentApp;

        /// <summary>
        /// 本控制方的 ID。官方 V4 里这个 ID 由中继服务器分配,App 再用 ?tid=控制方ID 挂载上来。
        /// 我们自己就是服务器,所以自己生成一个,每次启动游戏生成一次,二维码里的 tid 就是它。
        /// </summary>
        public static readonly string TargetId = Guid.NewGuid().ToString();

        /// <summary> 服务器分配给当前 App 连接的 clientId(每条连接一个) </summary>
        public string appClientId;

        /// <summary> 服务器运行状态 </summary>
        public static bool IsRunning = false;
        /// <summary> App连接状态 </summary>
        public static bool IsConn = false;

        /// <summary>
        /// 郊狼主机在 App 内的 slotId。V4 里 clientId 定位"哪个 App",slotId 定位"App 里的哪台设备",
        /// 操作指令两个都要带。App 接入后会上报 devices.snapshot,从中拿到这个值。
        /// </summary>
        public static string slotId = null;

        /// <summary> 服务器心跳定时器(V4 服务器每 30 秒向客户端发一次 heartbeat) </summary>
        private static Timer _heartbeatTimer;

        /// <summary>
        /// 生成二维码，并启动DGLAB
        /// </summary>
        public static void StartDGLAB()
        {
            QrCode.QRCode();
            StartServer();
        }
        public static void StopDGLAB()
        {
            try
            {
                StopWave();
                StopServer();
            }
            catch
            {
            }
        }
        /// <summary>
        /// 启动DGLAB服务器（在局域网中）
        /// </summary>
        public static void StartServer()
        {
            if (IsRunning)
            {
                Game1.activeClickableMenu = new DialogueBox($"操作无效,因为服务器已启动");
                return;
            }

            try
            {
                server = new WebSocketServer(ModEntry.Config.Port);
                server.AddWebSocketService<DGLABServer>("/");   // 兼容不带路径的直连
                server.AddWebSocketService<DGLABServer>("/v4"); // 与官方中继服务器路径一致
                server.Start();
                IsRunning = true;

                // V4 协议:服务器每 30 秒向客户端发一次心跳帧,客户端无需回复
                _heartbeatTimer?.Dispose();
                _heartbeatTimer = new Timer(_ =>
                {
                    try { _currentApp?.SendFrame(new { type = "heartbeat" }); } catch { }
                }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

                Game1.addHUDMessage(new HUDMessage("服务启动成功", 2));
            }
            catch (Exception ex)
            {
                if (ex is System.Net.Sockets.SocketException socketEx && socketEx.ErrorCode == 10048)
                {
                    Game1.activeClickableMenu = new DialogueBox("服务启动失败: 因为端口已被占用, 请尝试换个端口或启用自动选择端口");
                }
                else if (ex is InvalidOperationException && ex.InnerException is System.Net.Sockets.SocketException innerSocketEx && innerSocketEx.ErrorCode == 10048)
                {
                    Game1.activeClickableMenu = new DialogueBox("服务启动失败: 因为端口已被占用, 请尝试换个端口或启用自动选择端口");
                }
                else
                {
                    Game1.activeClickableMenu = new DialogueBox($"服务启动失败:{ex.Message}");
                }
                Console.WriteLine(ex);
            }
        }
        /// <summary>
        /// App 连接建立时调用。此时要完成 V4 的接入握手。
        /// </summary>
        protected override void OnOpen()
        {
            // V4 的 App 通过 ws://ip:port/v4?tid=控制方ID 连接,先校验 tid
            string tid = QueryString["tid"];
            if (tid != TargetId)
            {
                // 4001 = controller_not_found(V4 业务关闭码)
                Close(4001, "controller_not_found");
                return;
            }

            appClientId = Guid.NewGuid().ToString();
            _currentApp = this;
            IsConn = true;

            // 按 V4 协议,App 接入后服务器要依次发 hello 和 controller_attached,
            // App 收到 controller_attached 之后才会上报 devices.snapshot
            SendFrame(new { type = "hello", clientId = appClientId });
            SendFrame(new { type = "controller_attached", clientId = TargetId });

            Game1.addHUDMessage(new HUDMessage("App连接成功", 1));

            // 主动请求一次设备列表,防止漏掉 snapshot
            SendRpc("devices.get", null);
        }
        /// <summary>
        /// App 连接断开 调用此函数
        /// </summary>
        /// <param name="e"></param>
        protected override void OnClose(CloseEventArgs e)
        {
            // 只有当前生效的 App 连接断开时才更新状态
            // (tid 校验失败被拒绝的连接也会走到这里,那种情况不用管)
            if (_currentApp == this)
            {
                _currentApp = null;
                IsConn = false;
                slotId = null;
                _pulseBusyUntilTicksA = 0;
                _pulseBusyUntilTicksB = 0;
                Game1.addHUDMessage(new HUDMessage("App连接已断开", 3));
            }
        }
        /// <summary>
        /// App 发送消息时调用,处理消息
        /// </summary>
        /// <param name="e"></param>
        protected override void OnMessage(MessageEventArgs e)
        {
            base.OnMessage(e);
            if (!e.IsText) return;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(e.Data); }
            catch { return; }

            using (doc)
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                string type = root.TryGetProperty("type", out JsonElement tEl) ? tEl.GetString() : null;

                // 服务器级 ping,回复 pong(V4 里它不转发给对端)
                if (type == "ping")
                {
                    SendFrame(new { type = "pong", ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                    return;
                }

                // 应用层消息都在 {"type":"message","clientId":...,"data":{...}} 里
                if (type != "message") return;
                if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object) return;

                string t = data.TryGetProperty("t", out JsonElement ttEl) ? ttEl.GetString() : null;
                switch (t)
                {
                    case "ev":   // App 主动上报的事件
                        HandleEvent(data);
                        break;
                    case "resp": // RPC 响应
                        HandleResponse(data);
                        break;
                }
            }
        }
        /// <summary>
        /// 处理 App 上报的事件(devices.snapshot / devices.patch / slots.patch)
        /// </summary>
        private void HandleEvent(JsonElement data)
        {
            string ev = data.TryGetProperty("ev", out JsonElement evEl) ? evEl.GetString() : null;
            switch (ev)
            {
                case "devices.snapshot": // App 接入后上报全量设备列表
                    if (data.TryGetProperty("devices", out JsonElement devs) && devs.ValueKind == JsonValueKind.Array)
                        UpdateDevices(devs);
                    break;
                case "devices.patch": // 设备列表增量
                    if (data.TryGetProperty("added", out JsonElement added) && added.ValueKind == JsonValueKind.Array)
                        UpdateDevices(added);
                    if (data.TryGetProperty("removed", out JsonElement removed) && removed.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement r in removed.EnumerateArray())
                        {
                            if (r.GetString() == slotId)
                            {
                                slotId = null;
                                Game1.addHUDMessage(new HUDMessage("郊狼设备已移除", 3));
                            }
                        }
                    }
                    break;
                case "slots.patch": // 设备状态增量(软上限变化会走这里)
                    if (data.TryGetProperty("slots", out JsonElement slots) && slots.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement slot in slots.EnumerateArray())
                            UpdateSlotLimits(slot);
                    }
                    break;
            }
        }
        /// <summary>
        /// 处理 RPC 响应。device.op 的响应要等任务结束才返回,这里没有需要等待结果的地方,
        /// 只关心错误和 devices.get 的响应。
        /// </summary>
        private void HandleResponse(JsonElement data)
        {
            if (data.TryGetProperty("error", out JsonElement err))
            {
                Console.WriteLine("DGLAB 指令执行失败: " + err.GetString());
                return;
            }
            // devices.get 的响应 result.devices 里带设备列表
            if (data.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("devices", out JsonElement devs) && devs.ValueKind == JsonValueKind.Array)
            {
                UpdateDevices(devs);
            }
        }
        /// <summary>
        /// 从设备列表中找出第一台郊狼主机,记录它的 slotId
        /// </summary>
        private void UpdateDevices(JsonElement devices)
        {
            foreach (JsonElement dev in devices.EnumerateArray())
            {
                string type = dev.TryGetProperty("type", out JsonElement ty) ? ty.GetString() : null;
                string sid = dev.TryGetProperty("slotId", out JsonElement si) ? si.GetString() : null;
                if (sid == null || type == null) continue;
                if (!type.StartsWith("COYOTE")) continue; // 只接管郊狼主机,负鼠/灵猫等设备跳过

                if (slotId != sid)
                {
                    slotId = sid;
                    Game1.addHUDMessage(new HUDMessage("已发现郊狼设备", 1));
                }
                if (dev.TryGetProperty("slotState", out JsonElement ss))
                    ReadLimitsFromSlotState(ss);
                return; // 只接管第一台郊狼
            }
        }
        /// <summary>
        /// 从 slots.patch 中更新软上限
        /// </summary>
        private void UpdateSlotLimits(JsonElement slot)
        {
            string sid = slot.TryGetProperty("slotId", out JsonElement si) ? si.GetString() : null;
            if (sid == null || sid != slotId) return;
            if (slot.TryGetProperty("slotState", out JsonElement ss))
                ReadLimitsFromSlotState(ss);
        }
        /// <summary>
        /// 停止服务器
        /// </summary>
        public static void StopServer()
        {
            if (!IsRunning)
            {
                Game1.addHUDMessage(new HUDMessage("关闭操作无效，因为服务器未运行", 3));
                return;
            }

            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            server.Stop();
            StopWave();
            IsConn = false;
            IsRunning = false;
            slotId = null;

            Game1.addHUDMessage(new HUDMessage("服务关闭成功", 2));
        }
        #endregion
        #region V4 发送基础
        /// <summary> RPC 请求 ID 计数器(同一 App 下未完成的请求 reqId 不能重复) </summary>
        private static int _reqIdCounter = 0;
        private static string NextReqId() => "req-" + Interlocked.Increment(ref _reqIdCounter);

        /// <summary> 给当前连接发送一个服务器帧 </summary>
        private void SendFrame(object frame)
        {
            try { Send(JsonSerializer.Serialize(frame)); } catch { }
        }
        /// <summary>
        /// 给当前 App 发送应用层消息。外层固定为 {"type":"message","clientId":...,"data":...},
        /// clientId 填控制方(我们)的 ID,App 用它把响应路由回来。
        /// </summary>
        private static void SendToApp(object rpcPayload)
        {
            DGLABServer app = _currentApp;
            if (app == null) return;
            app.SendFrame(new { type = "message", clientId = TargetId, data = rpcPayload });
        }
        /// <summary> 发送一个 RPC 请求 </summary>
        private static void SendRpc(string method, object data)
        {
            if (data == null)
                SendToApp(new { t = "req", reqId = NextReqId(), m = method });
            else
                SendToApp(new { t = "req", reqId = NextReqId(), m = method, data });
        }
        /// <summary>
        /// 发送 device.op 设备操作任务。
        /// actionType: 0=AppendPulseData(波形) 3=AddIntensity(相对增减) 4=SetTempIntensity(临时强度) 7=SetIntensity(归零)
        /// channel: 0=A通道 1=B通道
        /// </summary>
        private static void SendOperate(int actionType, int channel, object value, int durationMs = 0, bool immediate = false)
        {
            SendRpc("device.op", new
            {
                s = slotId,        // 目标设备 slotId
                t = actionType,    // 操作类型
                c = channel,       // 通道
                p = 1,             // 优先级(默认 1)
                d = durationMs,    // 持续时间 ms,d=0 表示不自动结束
                im = immediate,    // 是否替换同设备、同通道、同类型的已有任务
                v = value          // 强度值或波形帧列表
            });
        }
        #endregion
        #region 强度
        static public void SendStrength()
        {
            DGLABServer.SendStrengthACommand(Control.EndStrengthA);
            DGLABServer.SendStrengthBCommand(Control.EndStrengthB);
        }
        static public void SendStrengthACommand(int strength)
        {
            if (!IsRunning || !IsConn || slotId == null) return;
            // V4 没有"直接设置绝对强度"的指令(SetIntensity 只接受 0),
            // 用 SetTempIntensity(临时强度) + im:true(替换旧的强度任务) + d:0(不自动结束)
            // 实现和 V3 的 strength-1+2+X 完全相同的"设定绝对强度"效果
            SendOperate(4, 0, strength, 0, true);
        }
        static public void SendStrengthBCommand(int strength)
        {
            if (!IsRunning || !IsConn || slotId == null) return;
            SendOperate(4, 1, strength, 0, true);
        }
        #endregion
        #region 波形
        /// <summary>
        /// 波形发送冷却时间,单位1/60秒
        /// </summary>
        public static int waveCD = 0;
        /// <summary> A/B 通道当前波形任务预计播完的时间(Tick,0 表示空闲)(V4 波形在 App 内排队,不能无限制塞) </summary>
        private static long _pulseBusyUntilTicksA = 0;
        private static long _pulseBusyUntilTicksB = 0;
        /// <summary>
        /// 发出波形
        /// </summary>
        /// <param name="wave">配置里的波形,JSON 数组形式的十六进制帧列表,例如 ["0A0A0A0A64646464", ...]</param>
        /// <param name="rightNow">是否跳过冷却立即发送</param>
        static public void SendWaveCommand(string wave, bool rightNow = false)
        {
            if (!IsRunning || !IsConn || slotId == null) return;
            if (!rightNow)
            {
                if (waveCD > 0) return;
                waveCD = 30;//在这里控制实际冷却时间
            }

            // 配置里的波形就是 V3 帧格式的十六进制字符串数组,V4 的 AppendPulseData 直接支持
            string[] frames;
            try { frames = JsonSerializer.Deserialize<string[]>(wave); }
            catch { return; }
            if (frames == null || frames.Length == 0) return;

            int durationMs = frames.Length * 100; // V4 每 100ms 消费一帧
            long nowTicks = DateTime.Now.Ticks;
            const long earlyRefillTicks = 150 * TimeSpan.TicksPerMillisecond; // 剩 150ms 就补下一段

            // V3 是往 App 的波形队列里追加(上限 500 帧),V4 的波形是排队执行的任务。
            // 如果像惩罚循环那样每 16ms 塞一次,队列会越积越长、输出越来越滞后。
            // 所以改成:当前一段还剩 150ms 就要播完时才补下一段,队列里最多积压一段,
            // 听感上和 V3 的连续输出一致。
            if (ModEntry.Config.EnableA && nowTicks >= _pulseBusyUntilTicksA - earlyRefillTicks)
            {
                SendOperate(0, 0, frames, durationMs);
                _pulseBusyUntilTicksA = Math.Max(_pulseBusyUntilTicksA, nowTicks) + durationMs * TimeSpan.TicksPerMillisecond;
            }
            if (ModEntry.Config.EnableB && nowTicks >= _pulseBusyUntilTicksB - earlyRefillTicks)
            {
                SendOperate(0, 1, frames, durationMs);
                _pulseBusyUntilTicksB = Math.Max(_pulseBusyUntilTicksB, nowTicks) + durationMs * TimeSpan.TicksPerMillisecond;
            }
        }
        /// <summary>
        /// 清除波形队列
        /// </summary>
        static public void StopWave()
        {
            if (!IsRunning || !IsConn || slotId == null) return;

            // V4 的 device.op.clear 会清掉指定通道上的所有任务——包括强度任务,
            // 而 V3 的 clear-1/clear-2 只清波形队列、不动强度。
            // 所以清完要立刻把当前强度重新下发,否则强度会掉成 0
            SendRpc("device.op.clear", new { s = slotId, c = 0 });
            SendRpc("device.op.clear", new { s = slotId, c = 1 });
            _pulseBusyUntilTicksA = 0;
            _pulseBusyUntilTicksB = 0;
            SendStrength();
        }
        #endregion
        #region 读取 App 软上限消息
        /// <summary> A通道强度软上限 </summary>
        public static int SoftA = 0;
        /// <summary> B通道强度软上限 </summary>
        public static int SoftB = 0;
        /// <summary>
        /// 从设备的 slotState 中读取软上限。
        /// 优先用 comfortLimit.absoluteMax(对应 App 里的强度上限滑条),没有再用 intensityMax。
        /// </summary>
        private void ReadLimitsFromSlotState(JsonElement slotState)
        {
            int? a = ReadChannelLimit(slotState, "channelA");
            if (a.HasValue) SoftA = a.Value;
            int? b = ReadChannelLimit(slotState, "channelB");
            if (b.HasValue) SoftB = b.Value;
        }
        private static int? ReadChannelLimit(JsonElement slotState, string channelName)
        {
            if (!slotState.TryGetProperty(channelName, out JsonElement ch) || ch.ValueKind != JsonValueKind.Object)
                return null;
            if (ch.TryGetProperty("comfortLimit", out JsonElement cl) && cl.ValueKind == JsonValueKind.Object)
            {
                if (cl.TryGetProperty("absoluteMax", out JsonElement am) && am.ValueKind == JsonValueKind.Number)
                    return am.GetInt32();
            }
            if (ch.TryGetProperty("intensityMax", out JsonElement im) && im.ValueKind == JsonValueKind.Number)
                return im.GetInt32();
            return null;
        }
        #endregion
    }
}
