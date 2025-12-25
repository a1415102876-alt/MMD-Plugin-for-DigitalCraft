using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace CharaAnime
{
    public class VmdReader
    {
        // 1. 统一缓存 Shift-JIS 编码，避免每次 Load 都反射查找
        private static readonly Encoding ShiftJisEncoding;

        static VmdReader()
        {
            try
            {
                Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                ShiftJisEncoding = Encoding.GetEncoding("shift_jis");
            }
            catch
            {
                // 回退机制
                ShiftJisEncoding = Encoding.UTF8;
            }
        }

        public static VmdData LoadVmd(string filePath)
        {
            if (!File.Exists(filePath)) return null;

            VmdData data = new VmdData();

            try
            {
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    // 【优化核心】准备复用的 Buffer，避免在循环中反复 new 数组
                    byte[] buffer64 = new byte[64];
                    byte[] buffer24 = new byte[24];

                    // 1. Header
                    string version = ReadStringFixed(br, 30, ShiftJisEncoding);
                    int nameLen = version.StartsWith("Vocaloid Motion Data 0002") ? 20 : 10;
                    data.ModelName = ReadStringFixed(br, nameLen, ShiftJisEncoding);

                    // 2. Bone Frames
                    int boneCount = br.ReadInt32();
                    Console.WriteLine($"[VMD] Bone frames: {boneCount}");

                    // 【优化】指定 List 容量，避免多次扩容复制
                    data.BoneFrames = new List<VmdBoneFrame>(boneCount);

                    for (int i = 0; i < boneCount; i++)
                    {
                        VmdBoneFrame frame = new VmdBoneFrame();
                        frame.Name = ReadStringFixed(br, 15, ShiftJisEncoding);
                        frame.FrameNo = br.ReadInt32();
                        frame.Position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        frame.Rotation = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                        // 【优化】读取到共享 Buffer，再拷贝。避免 br.ReadBytes(64) 产生垃圾对象
                        br.Read(buffer64, 0, 64);
                        frame.Curve = new byte[64];
                        Buffer.BlockCopy(buffer64, 0, frame.Curve, 0, 64);

                        data.BoneFrames.Add(frame);
                    }

                    // 3. Morph Frames (检查文件流位置)
                    if (br.BaseStream.Position < br.BaseStream.Length)
                    {
                        int morphCount = br.ReadInt32();
                        Console.WriteLine($"[VMD] Morph frames: {morphCount}");
                        // 【优化】指定容量
                        data.MorphFrames = new List<VmdMorphFrame>(morphCount);

                        for (int i = 0; i < morphCount; i++)
                        {
                            VmdMorphFrame frame = new VmdMorphFrame();
                            frame.Name = ReadStringFixed(br, 15, ShiftJisEncoding);
                            frame.FrameNo = br.ReadInt32();
                            frame.Weight = br.ReadSingle();
                            data.MorphFrames.Add(frame);
                        }
                    }

                    // 4. Camera Frames
                    if (br.BaseStream.Position < br.BaseStream.Length)
                    {
                        int camCount = br.ReadInt32();
                        Console.WriteLine($"[VMD] Camera frames: {camCount}");
                        // 【优化】指定容量
                        data.CameraFrames = new List<VmdCameraFrame>(camCount);

                        for (int i = 0; i < camCount; i++)
                        {
                            VmdCameraFrame frame = new VmdCameraFrame();
                            frame.FrameNo = br.ReadInt32();
                            frame.Distance = br.ReadSingle();
                            frame.Position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                            float rotX = br.ReadSingle() * Mathf.Rad2Deg;
                            float rotY = br.ReadSingle() * Mathf.Rad2Deg;
                            float rotZ = br.ReadSingle() * Mathf.Rad2Deg;
                            frame.Rotation = new Vector3(rotX, rotY, rotZ);

                            // 【优化】使用 24字节 Buffer 复用
                            br.Read(buffer24, 0, 24);
                            frame.Curve = new byte[24];
                            Buffer.BlockCopy(buffer24, 0, frame.Curve, 0, 24);

                            frame.Fov = (float)br.ReadInt32();
                            frame.Orthographic = br.ReadByte() != 0;

                            data.CameraFrames.Add(frame);
                        }
                    }

                    // 5. Light Frames
                    if (br.BaseStream.Position < br.BaseStream.Length)
                    {
                        int lightCount = br.ReadInt32();
                        // 快速跳过
                        long lightBytes = lightCount * 28L;
                        if (br.BaseStream.Position + lightBytes <= br.BaseStream.Length)
                        {
                            br.BaseStream.Seek(lightBytes, SeekOrigin.Current);
                        }
                    }

                    // 6. Shadow Frames
                    if (br.BaseStream.Position < br.BaseStream.Length)
                    {
                        int shadowCount = br.ReadInt32();
                        // 快速跳过
                        long shadowBytes = shadowCount * 9L;
                        if (br.BaseStream.Position + shadowBytes <= br.BaseStream.Length)
                        {
                            br.BaseStream.Seek(shadowBytes, SeekOrigin.Current);
                        }
                    }

                    // 7. IK Frames
                    if (br.BaseStream.Position < br.BaseStream.Length)
                    {
                        int ikCount = br.ReadInt32();
                        Console.WriteLine($"[VMD] IK frames: {ikCount}");
                        // 【优化】指定容量
                        data.IkFrames = new List<VmdIkFrame>(ikCount);

                        for (int i = 0; i < ikCount; i++)
                        {
                            VmdIkFrame frame = new VmdIkFrame();
                            frame.FrameNo = br.ReadInt32();
                            frame.Show = br.ReadByte() != 0;
                            int infoCount = br.ReadInt32();

                            for (int j = 0; j < infoCount; j++)
                            {
                                IkInfo info = new IkInfo();
                                info.Name = ReadStringFixed(br, 20, ShiftJisEncoding);
                                info.Enable = br.ReadByte() != 0;
                                frame.IkInfos.Add(info);
                            }
                            data.IkFrames.Add(frame);
                        }
                    }

                    Console.WriteLine($"[VMD] Parsed OK. Pos: {br.BaseStream.Position}/{br.BaseStream.Length}");
                }
                return data;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[VMD] Load Error: {e.Message}");
                return data; // 返回已解析的部分数据
            }
        }

        private static string ReadStringFixed(BinaryReader br, int length, Encoding encoding)
        {
            byte[] bytes = br.ReadBytes(length);
            int nullIndex = Array.IndexOf(bytes, (byte)0);
            if (nullIndex >= 0) return encoding.GetString(bytes, 0, nullIndex);
            return encoding.GetString(bytes);
        }

        // --- 数据结构 ---

        public class IkInfo
        {
            public bool Enable;
            public string Name;
        }

        public class VmdBoneFrame
        {
            public byte[] Curve;
            public int FrameNo;
            public string Name;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        public class VmdCameraFrame
        {
            public byte[] Curve;
            public float Distance;
            public float Fov;
            public int FrameNo;
            public bool Orthographic;
            public Vector3 Position;
            public Vector3 Rotation;
        }

        public class VmdData
        {
            public List<VmdBoneFrame> BoneFrames = new List<VmdBoneFrame>();
            public List<VmdCameraFrame> CameraFrames = new List<VmdCameraFrame>();
            public List<VmdIkFrame> IkFrames = new List<VmdIkFrame>();
            public string ModelName;
            public List<VmdMorphFrame> MorphFrames = new List<VmdMorphFrame>();
        }

        public class VmdIkFrame
        {
            public int FrameNo;
            public List<IkInfo> IkInfos = new List<IkInfo>();
            public bool Show;
        }

        public class VmdMorphFrame
        {
            public int FrameNo;
            public string Name;
            public float Weight;
        }
    }
}