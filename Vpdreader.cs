using System.Text;
using UnityEngine;

namespace CharaAnime
{
    public class VpdReader
    {
        public class VpdData
        {
            public string FileName; // 文件名作为标识
            public List<VpdBone> Bones = new List<VpdBone>();
        }

        public class VpdBone
        {
            public string Name;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        public static VpdData LoadVpd(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[VPD] File not found: {filePath}");
                return null;
            }

            VpdData data = new VpdData();
            data.FileName = Path.GetFileNameWithoutExtension(filePath);

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[VPD] Warning: Failed to register encoding provider. {e.Message}");
            }

            Encoding enc;
            try
            {
                enc = Encoding.GetEncoding("shift_jis");
            }
            catch
            {
                Console.WriteLine("[VPD] Shift-JIS not supported, falling back to UTF-8.");
                enc = Encoding.UTF8;
            }

            string[] lines = File.ReadAllLines(filePath, enc);

            if (lines.Length == 0 || !lines[0].StartsWith("Vocaloid Pose Data"))
            {
                Console.WriteLine("[VPD] Invalid header.");
                return null;
            }

            VpdBone currentBone = null;

            foreach (string line in lines)
            {
                string l = line.Trim();
                if (string.IsNullOrEmpty(l) || l.StartsWith("//")) continue;

                // 格式: BoneName{
                if (l.EndsWith("{"))
                {
                    string name = l.Substring(4, l.Length - 5);
                    currentBone = new VpdBone { Name = name };
                }
                // 格式: 1.0,2.0,3.0;
                // 格式: 0.0,0.0,0.0,1.0;
                else if (currentBone != null && l.EndsWith(";"))
                {
                    string valStr = l.TrimEnd(';');
                    string[] parts = valStr.Split(',');

                    if (parts.Length == 3) // Position
                    {
                        float x = float.Parse(parts[0]);
                        float y = float.Parse(parts[1]);
                        float z = float.Parse(parts[2]);
                        currentBone.Position = new Vector3(x, y, z);
                    }
                    else if (parts.Length == 4) // Rotation (Quaternion)
                    {
                        float x = float.Parse(parts[0]);
                        float y = float.Parse(parts[1]);
                        float z = float.Parse(parts[2]);
                        float w = float.Parse(parts[3]);
                        currentBone.Rotation = new Quaternion(x, y, z, w);
                    }
                }
                // 格式: }
                else if (l == "}")
                {
                    if (currentBone != null)
                    {
                        data.Bones.Add(currentBone);
                        currentBone = null;
                    }
                }
            }

            Console.WriteLine($"[VPD] Loaded {data.Bones.Count} bones from {data.FileName}");
            return data;
        }
    }
}