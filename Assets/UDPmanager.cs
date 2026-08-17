using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using System.IO;
using System.Text; 
public class UdpImageReceiver_UpdateDriven : MonoBehaviour
{
    public Texture2D tex;

    [Header("监听端口（与发送端保持一致）")]
    public int listenPort = 8001;

    [Header("与发送端相同的 payloadSizePerPacket（每包最大图像数据长度，不含 8 字节头）")]
    public int payloadSizePerPacket = 64000 - 8;

    [Header("接收到的图片要展示到哪个 RawImage 上（可留空，仅示范）")]
    public RawImage targetRawImage;

    // UDP 客户端和远端 EndPoint
    private UdpClient udpClient;
    private IPEndPoint remoteEndPoint;

    // 收分片时使用的字典：Key = imageIndex，Value = Dictionary<packetIndex, byte[]>（尚未拼接的分片集合）
    private Dictionary<int, Dictionary<int, byte[]>> imageChunks = new Dictionary<int, Dictionary<int, byte[]>>();

    // 已经收齐最后一包、等待主线程去拼接的分片集合：Key = imageIndex，Value = Dictionary<packetIndex, byte[]>
    private Dictionary<int, Dictionary<int, byte[]>> readyChunks = new Dictionary<int, Dictionary<int, byte[]>>();

    // 用来保护 imageChunks 与 readyChunks 的锁
    private readonly object dictLock = new object();

    private SynchronizationContext synchronizationContext;

    private string logFilePath;
    void Start()
    {
        logFilePath = Path.Combine(Application.dataPath, "manalog.csv");
        string header = "ImageIndex,PacketIndex,Time,Event";
        File.WriteAllText(logFilePath, header + Environment.NewLine, Encoding.UTF8);
        UnityEngine.Debug.Log($"Log file created at: {logFilePath}");

        // 先用无参构造，不要立刻绑定端口
        udpClient = new UdpClient(AddressFamily.InterNetwork);

        synchronizationContext = SynchronizationContext.Current;

        // 允许地址重用，这样如果之前有未完全关闭的同端口 socket，也能复用
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // 手动绑定到任意 IP 的指定端口
        udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, listenPort));

        // 远端 EndPoint 占位（等真正接收时 EndReceive 会填充）
        remoteEndPoint = new IPEndPoint(IPAddress.Parse("192.168.0.100"), 0);

        Recvive();

    }


    public void Recvive()
    {
        UnityEngine.Debug.Log("开始接收数据");
        Thread RecviveThread = new Thread(() =>
        {
            //开启异步接收data里面的数据
            udpClient.BeginReceive(CallBackRecvive, udpClient);//接收数据
        })
        {
            //设置为后台线程
            IsBackground = true
        };

        RecviveThread.Start();
    }

    private void CallBackRecvive(IAsyncResult ar)
    {   

        //结束异步接受 不结束会导致重复挂起线程卡死
        byte[] data = udpClient.EndReceive(ar, ref remoteEndPoint);
        int imageIndex = BitConverter.ToInt32(data, 0);
        int packetIndex = BitConverter.ToInt32(data, 4);
        LogEvent(imageIndex,packetIndex,"Callback()收到图像数据");
        DealPack(data);

        //再次开启异步接收数据
        udpClient.BeginReceive(CallBackRecvive, udpClient);
    }

    void Update()
    {
        // 检查是否有已“收齐分片、等待拼接”的 imageIndex
        List<int> toProcess = null;

        // 首先收集当前需要处理的 imageIndex
        lock (dictLock)
        {
            if (readyChunks.Count > 0)
            {
                // 把键复制到一个列表里，避免在拼接过程中操作字典出错
                toProcess = readyChunks.Keys.ToList();
            }
        }

        if (toProcess == null || toProcess.Count == 0)
        {
            // 没有要处理的，直接返回
            return;
        }

        // 对每个待处理的 imageIndex，都要：
        // 1) 从 readyChunks 拿到它对应的所有分片字典
        // 2) 按索引排序，拼成一个完整的 byte[]
        // 3) 用 Texture2D.LoadImage 解码
        // 4) 把得到的 Texture2D 赋给 RawImage（或其他渲染逻辑）
        // 5) 从 readyChunks 中移除该 entry

        foreach (int imageIndex in toProcess)
        {
            Dictionary<int, byte[]> chunksDict = null;

            lock (dictLock)
            {
                if (readyChunks.TryGetValue(imageIndex, out var dictCopy))
                {
                    // 拿到当前字典引用（注意此时不要立刻从 readyChunks 移除，否则后续锁住又检测不到）
                    chunksDict = dictCopy;
                    readyChunks.Remove(imageIndex);
                }
            }

            if (chunksDict == null)
            {
                // 如果其他线程已经把它移除了，跳过
                continue;
            }

            // 按 packetIndex 升序排序所有分片
            var orderedKeys = chunksDict.Keys.OrderBy(idx => idx).ToList();

            // 计算总长度
            int totalBytes = 0;
            foreach (int idx in orderedKeys)
            {
                totalBytes += chunksDict[idx].Length;
            }

            // 拼接字节流
            byte[] fullImageBytes = new byte[totalBytes];
            Debug.Log($"开始试着解析，这次共有{totalBytes}数据长度");
            int offset = 0;
            foreach (int idx in orderedKeys)
            {
                byte[] chunk = chunksDict[idx];
                Buffer.BlockCopy(chunk, 0, fullImageBytes, offset, chunk.Length);
                offset += chunk.Length;
            }

            // 用 LoadImage 解码（务必在主线程里执行）
            Texture2D tex = new Texture2D(2, 2);
            tex.LoadRawTextureData(fullImageBytes);
            bool loaded = tex.LoadImage(fullImageBytes);

            if (loaded)
            {
                Debug.Log($"[Receiver][Update] 成功拼接并解码图像 #{imageIndex}，总字节数 = {fullImageBytes.Length}");
                // 如果有指定 RawImage，就把纹理赋上去
                if (targetRawImage != null)
                {
                    targetRawImage.texture = tex;
                    targetRawImage.SetNativeSize();
                }
                else
                {
                    // 没有 RawImage，暂时只打印日志
                    Debug.Log($"[Receiver] 没有指定 targetRawImage，图像 #{imageIndex} 解码完成，但未渲染。");
                }
            }
            else
            {
                Debug.Log($"[Receiver][Update] LoadImage 失败，图像 #{imageIndex} 数据可能损坏。");
            }

            // 如果你还想做“把 tex 保存到本地磁盘”，也可以在这里：
            // string path = Application.dataPath + $"/Received_{imageIndex}.png";
            // System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
            // Debug.Log($"[Receiver] 图像已保存到 {path}");
        }
    }
    void DealPack(byte[] packet)
    {
        if (packet.Length < 8)
        {
            Debug.LogWarning($"[Receiver] 收到的包小于 8 字节头，丢弃。长度={packet.Length}");
            return;
        }

        // 解析头部：前 8 字节。先取两个 int。
        //  - 前 4 字节：imageIndex（第几张图）
        //  - 后 4 字节：packetIndex（本张图的第几包，从 0 开始）
        int imageIndex = BitConverter.ToInt32(packet, 0);
        int packetIndex = BitConverter.ToInt32(packet, 4);
        LogEvent(imageIndex,packetIndex,"mana中DealPack()收到图像数据");
        // 真正的图像分片从 offset=8 开始
        const int headerSize = 8;
        int dataLen = packet.Length - headerSize;
        byte[] chunkData = new byte[dataLen];

        Buffer.BlockCopy(packet, headerSize, chunkData, 0, dataLen);
        Debug.Log($"收到第{imageIndex}张图片的第{packetIndex}个包数据，长度为{dataLen}");

        // 把分片缓存到 imageChunks，必要时创建子字典
        lock (dictLock)
        {
            if (!imageChunks.ContainsKey(imageIndex))
            {
                imageChunks[imageIndex] = new Dictionary<int, byte[]>();
            }

            // 插入或覆盖这一索引对应的分片
            imageChunks[imageIndex][packetIndex] = chunkData;
            // 如果 dataLen < payloadSizePerPacket，则说明这是该图的最后一包，整个图可以在主线程里去拼接
            if (dataLen < payloadSizePerPacket)
            {
                // 把这张图剩余已经收到的所有分片字典，移到 readyChunks 中
                readyChunks[imageIndex] = imageChunks[imageIndex];
                LogEvent(imageIndex,packetIndex,"处理完图像,转给SendToRemote函数");
                SendToRemote(imageIndex, packetIndex);
                // 从 imageChunks 中移除，释放空间
                imageChunks.Remove(imageIndex);
            }
        }
    }
    void SendToRemote(int imageIndex, int packetIndex)
    {
        LogEvent(imageIndex,packetIndex,"SendToRemote函数收到图像");
        // 2) 准备 85 个 float 示例数据
        int floatCount = 85;
        float[] floatArray = new float[floatCount];
        for (int i = 0; i < floatCount; i++)
        {
            floatArray[i] = i * 0.5f + 1.0f; // 举例：1.0, 1.5, 2.0, … 
        }

        // 3) 申请总长度为 348 字节的 byte[]
        int totalLength = 4 + 4 + floatCount * 4; // = 348
        byte[] result = new byte[totalLength];

        // 4) 把 imageIndex（int，4 字节）写到 result[0..3]
        byte[] imageBytes = BitConverter.GetBytes(imageIndex);
        // 默认 BitConverter 返回的是“本机字节序（little-endian）”
        Array.Copy(imageBytes, 0, result, 0, 4);

        // 5) 把 packetIndex（int，4 字节）写到 result[4..7]
        byte[] packetBytes = BitConverter.GetBytes(packetIndex);
        Array.Copy(packetBytes, 0, result, 4, 4);

        // 6) 从 offset = 8 开始，逐个把 85 个 float 转成 4 字节，依次写入
        for (int i = 0; i < floatCount; i++)
        {
            byte[] floatBytes = BitConverter.GetBytes(floatArray[i]);
            int offset = 8 + i * 4;
            Array.Copy(floatBytes, 0, result, offset, 4);
        }
        LogEvent(imageIndex,packetIndex,"SendToRemote处理完成发送回去");
        udpClient.Send(result, result.Length, remoteEndPoint);
    }
    private void OnApplicationQuit()
    {
        if (udpClient != null)
        {
            udpClient.Close();
            udpClient = null;
        }
    }
    private void LogEvent(int ImageIndex, int PacketIndex, string eventType)
        {
            // 时间格式：yyyy-MM-dd HH:mm:ss.fff  （毫秒）
            string timeStr = DateTime.Now.ToString("HH:mm:ss.fffff");
            // 构造一行 CSV：逗号分隔
            // 例：2025-06-05 14:23:51.123,Sent,1024,
            string line = $"\"{ImageIndex}\",\"{PacketIndex}\",\"{timeStr}\",\"{eventType}\",PC端manager";


            // 追加到文件末尾
            try
            {
                // 注意：File.AppendAllText 内部会自动打开/写入/关闭
                File.AppendAllText(logFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // 如果日志写入本身出错，可以考虑把错误输出到控制台或别的地方
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 无法写日志：{ex.Message}");
            }
        }
        
        private void LogEvent(string eventType)
        {
            // 备注字段如果为 null，就写空字符串
            // 时间格式：yyyy-MM-dd HH:mm:ss.fff  （毫秒）
            string timeStr = DateTime.Now.ToString("HH:mm:ss.fffff");
            // 构造一行 CSV：逗号分隔
            // 例：2025-06-05 14:23:51.123,Sent,1024,
            string line = $"\"无\",\"无\",\"{timeStr}\",\"{eventType}\",PC端manager";

            // 追加到文件末尾
            try
            {
                // 注意：File.AppendAllText 内部会自动打开/写入/关闭
                File.AppendAllText(logFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // 如果日志写入本身出错，可以考虑把错误输出到控制台或别的地方
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 无法写日志：{ex.Message}");
            }
        }
}
