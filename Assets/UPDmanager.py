#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import socket
import threading
import struct
import time
import csv
import os
from io import BytesIO
from PIL import Image
from datetime import datetime

class UdpImageReceiver:
    def __init__(self,
                 listen_port: int = 8001,
                 payload_size_per_packet: int = 64000 - 8,
                 log_filename: str = "manalog.csv"):
        self.listen_port = listen_port
        self.payload_size = payload_size_per_packet
        self.log_path = os.path.abspath(log_filename)

        self.image_chunks = {}
        self.ready_chunks = {}
        
        self.lock = threading.Lock()
        self.running = True

#日志
        with open(self.log_path, "w", newline='', encoding='utf-8') as f:
            writer = csv.writer(f)
            writer.writerow(["ImageIndex", "PacketIndex", "Time", "Event"])
        print(f"文件创建在{self.log_path}")
#创建UDPsocket
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.sock.bind(("", self.listen_port))
        print(f"[Receiver] 监听端口UDP port {self.listen_port}")

#启动接收线程
        self.recv_thread = threading.Thread(target=self._recv_loop, daemon=True)
        self.recv_thread.start()
#启动处理线程
        self.proc_thread = threading.Thread(target=self._process_loop, daemon=True)
        self.proc_thread.start()

    def _log_event(self, image_index, packet_index, event_type):

        now_str = datetime.now().strftime("%Y-%m-%d %H:%M:%S.%f")
        with open(self.log_path, "a", newline='', encoding='utf-8') as f:
            writer = csv.writer(f)
            writer.writerow([image_index, packet_index, now_str, event_type])

    def _recv_loop(self):
        """不断接收 UDP 数据包"""
        while self.running:
            try:
                data, addr = self.sock.recvfrom(self.payload_size + 8)
                image_index, packet_index = struct.unpack('<II', data[:8])
                self._log_event(image_index, packet_index, f"接收到来自{addr}的宝")
                self._handle_packet(data, addr)
            except Exception as e:
                if self.running:
                    print(f"[Receiver] Error receiving packet: {e}")

    def _handle_packet(self, packet: bytes, addr):
        """解析头部，存储分片，并在接收最后一包时标记 ready"""
        if len(packet) < 8:
            print(f"[Warning] Packet 太小,不正确({len(packet)} bytes)")
            return

        image_index, packet_index = struct.unpack("<ii", packet[:8])
        self._log_event(image_index, packet_index, "handler数据包")

        # 提取有效chunk
        chunk = packet[8:]
        with self.lock:
            # 插入index
            self.image_chunks.setdefault(image_index, {})[packet_index] = chunk

            # 若这是最后一包，标记为 ready
            if len(chunk) < self.payload_size:
                self.ready_chunks[image_index] = self.image_chunks.pop(image_index)
                self._log_event(image_index, packet_index, "Marked ready for assembly")
                # 异步发送回 float 数据
                threading.Thread(target=self._send_floats, args=(image_index, packet_index, addr), daemon=True).start()

    def _process_loop(self):
        """主线程定期扫描 ready_chunks，将分片拼接、解码、展示/保存"""
        while self.running:
            to_process = []
            with self.lock:
                to_process = list(self.ready_chunks.keys())

            for idx in to_process:
                with self.lock:
                    chunks_dict = self.ready_chunks.pop(idx, None)
                if not chunks_dict:
                    continue

                # 按包序拼接
                ordered = [chunks_dict[i] for i in sorted(chunks_dict)]
                full_bytes = b"".join(ordered)
                self._log_event(idx, 0, f"拼接图像(total {len(full_bytes)} bytes)")

                # 用Pillow解码假设是JPEG 流
                try:
                    img = Image.open(BytesIO(full_bytes))
                    save_path = f"Received_{idx}.png"
                    img.save(save_path)
                    print(f"[Receiver] Image #{idx} 保存到{self.log_path}")
                    self._log_event(idx, 0, "图像编码完成并保存")
                except Exception as e:
                    print(f"[Receiver] 无法编码图像 #{idx}: {e}")
                    self._log_event(idx, 0 , "无法编码图像")

            time.sleep(0.1)

    def _send_floats(self, image_index, packet_index, addr):
        """构造 85 个float 并通过 UDP 发回发送端"""
        count = 85
        floats = [i * 0.5 + 1.0 for i in range(count)]
        # 前 4 字节 image_index, 接着 4 字节 packet_index, 然后 85×4 字节 float
        payload = struct.pack("<ii" + "f"*count, image_index, packet_index, *floats)
        try:
            self.sock.sendto(payload, addr)
            self._log_event(image_index, packet_index, "发送浮点给HoloLens")
        except Exception as e:
            print(f"[Receiver] Error sending floats: {e}")
            self._log_event(image_index, packet_index, f"发送浮点error: {e}")

    def stop(self):
        self.running = False
        self.sock.close()
        print("[Receiver] 停止")

if __name__ == "__main__":
    receiver = UdpImageReceiver()

    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        receiver.stop()
