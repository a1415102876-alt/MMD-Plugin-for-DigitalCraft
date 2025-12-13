using System.Text;
using UnityEngine;

namespace CharaAnime
{
    public class VmdReader
    {
        public static VmdData LoadVmd(string filePath)
        {
            if (!File.Exists(filePath)) return null;

            VmdData data = new VmdData();
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            // MMD 文件通常使用 Shift-JIS 编码
            Encoding shiftJis = Encoding.GetEncoding("shift_jis");

            try
            {
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (BinaryReader br = new BinaryReader(fs))
                {
                    // 1. Header
                    string version = ReadStringFixed(br, 30, shiftJis);
                    int nameLen = version.StartsWith("Vocaloid Motion Data 0002") ? 20 : 10;
                    data.ModelName = ReadStringFixed(br, nameLen, shiftJis);

                    // 2. Bone Frames
                    int boneCount = br.ReadInt32();
                    for (int i = 0; i < boneCount; i++)
                    {
                        VmdBoneFrame frame = new VmdBoneFrame();
                        frame.Name = ReadStringFixed(br, 15, shiftJis);
                        frame.FrameNo = br.ReadInt32();
                        frame.Position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        frame.Rotation = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        frame.Curve = br.ReadBytes(64);
                        data.BoneFrames.Add(frame);
                    }

                    // 3. Morph Frames
                    int morphCount = br.ReadInt32();
                    for (int i = 0; i < morphCount; i++)
                    {
                        VmdMorphFrame frame = new VmdMorphFrame();
                        frame.Name = ReadStringFixed(br, 15, shiftJis);
                        frame.FrameNo = br.ReadInt32();
                        frame.Weight = br.ReadSingle();
                        data.MorphFrames.Add(frame);
                    }

                    // 4. Camera Frames (根据 vmdlib.py 逻辑优化)
                    int camCount = br.ReadInt32();
                    // Console.WriteLine($"[VMD] Loading {camCount} Camera frames...");
                    for (int i = 0; i < camCount; i++)
                    {
                        VmdCameraFrame frame = new VmdCameraFrame();
                        frame.FrameNo = br.ReadInt32();
                        frame.Distance = br.ReadSingle(); // Distance (Target Distance)
                        frame.Position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()); // Position (LookAt Point)

                        float rotX = br.ReadSingle() * Mathf.Rad2Deg;
                        float rotY = br.ReadSingle() * Mathf.Rad2Deg;
                        float rotZ = br.ReadSingle() * Mathf.Rad2Deg;
                        frame.Rotation = new Vector3(rotX, rotY, rotZ);

                        frame.Curve = br.ReadBytes(24); // Interpolation Curve
                        frame.Fov = (float)br.ReadInt32(); // FOV (Degrees)
                        frame.Orthographic = br.ReadByte() != 0; // 0: Perspective, 1: Orthographic

                        data.CameraFrames.Add(frame);
                    }

                    // 5. Light Frames (跳过)
                    int lightCount = br.ReadInt32();
                    br.BaseStream.Seek(lightCount * 28, SeekOrigin.Current);

                    // 6. Shadow Frames (跳过)
                    int shadowCount = br.ReadInt32();
                    br.BaseStream.Seek(shadowCount * 9, SeekOrigin.Current);

                    // 7. IK Frames
                    if (br.BaseStream.Position < br.BaseStream.Length)
                    {
                        int ikCount = br.ReadInt32();
                        // Console.WriteLine($"[VMD] Loading {ikCount} IK frames...");
                        for (int i = 0; i < ikCount; i++)
                        {
                            VmdIkFrame frame = new VmdIkFrame();
                            frame.FrameNo = br.ReadInt32();
                            frame.Show = br.ReadByte() != 0;
                            int infoCount = br.ReadInt32();
                            for (int j = 0; j < infoCount; j++)
                            {
                                IkInfo info = new IkInfo();
                                info.Name = ReadStringFixed(br, 20, shiftJis);
                                info.Enable = br.ReadByte() != 0;
                                frame.IkInfos.Add(info);
                            }
                            data.IkFrames.Add(frame);
                        }
                    }
                }
                return data;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[VMD] Error: {e.Message}");
                return null;
            }
        }

        private static string ReadStringFixed(BinaryReader br, int length, Encoding encoding)
        {
            byte[] bytes = br.ReadBytes(length);
            int nullIndex = Array.IndexOf(bytes, (byte)0);
            if (nullIndex >= 0) return encoding.GetString(bytes, 0, nullIndex);
            return encoding.GetString(bytes);
        }

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