using UnityEngine;

namespace CharaAnime
{
    public class MmddAudioPlayer : MonoBehaviour
    {
        public MmddAudioPlayer(IntPtr ptr) : base(ptr)
        {
        }

        public AudioSource source;

        // =========================================================
        //添加可读写的 Time 属性
        // =========================================================
        public float Time
        {
            get => (source != null) ? source.time : 0f;
            set
            {
                if (source != null && source.clip != null)
                {
                    // 1. 限制在音频长度范围内
                    float clampTime = Mathf.Clamp(value, 0f, source.clip.length - 0.01f);

                    // 2. 设置时间
                    source.time = clampTime;

                    // 3. 如果正在“逻辑暂停”，必须同步更新 pauseTime
                    // 否则下次 Resume() 时，逻辑可能会用旧的 pauseTime 覆盖掉你刚设置的新时间
                    if (isPaused)
                    {
                        pauseTime = clampTime;
                    }
                }
            }
        }

        public bool IsPlaying => source != null && source.isPlaying;
        public float CurrentTime => Time; // 兼容旧代码

        // === 暂停状态 ===
        private bool isPaused = false;

        private float pauseTime = 0f;

        public static MmddAudioPlayer Install(GameObject container)
        {
            var player = container.GetComponent<MmddAudioPlayer>();
            if (player == null) player = container.AddComponent<MmddAudioPlayer>();

            player.source = container.GetComponent<AudioSource>();
            if (player.source == null) player.source = container.AddComponent<AudioSource>();
            player.source.spatialBlend = 0f; // 2D
            player.source.playOnAwake = false; // 防止自动播放
            return player;
        }

        public void Play(string filePath)
        {
            if (source == null) return;
            source.Stop();
            isPaused = false;

            Console.WriteLine($"[Audio] Manual Loading: {filePath}");

            try
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);

                // 2. 解析 WAV 头部
                int channels = fileBytes[22];
                int frequency = BitConverter.ToInt32(fileBytes, 24);

                // 寻找 "data" 块
                int pos = 12;
                while (pos < fileBytes.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(fileBytes, pos, 4);
                    int size = BitConverter.ToInt32(fileBytes, pos + 4);
                    if (id == "data")
                    {
                        pos += 8;
                        break;
                    }
                    pos += 8 + size;
                }

                // 3. 提取数据
                int sampleCount = (fileBytes.Length - pos) / 2;
                if (channels == 2) sampleCount /= 2;

                float[] data = new float[sampleCount * channels];

                int i = 0;
                while (pos < fileBytes.Length - 1 && i < data.Length)
                {
                    short val = BitConverter.ToInt16(fileBytes, pos);
                    data[i] = val / 32768.0f;
                    pos += 2;
                    i++;
                }

                // 4. 创建 & 播放
                AudioClip clip = AudioClip.Create("ExternalWav", sampleCount, channels, frequency, false);
                clip.SetData(data, 0);

                source.clip = clip;
                source.time = 0f; // 确保从头开始
                source.Play();

                Console.WriteLine($"[Audio] SUCCESS! Length: {clip.length:F2}s");
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Audio] Manual Load Failed: {e.Message}");
            }
        }

        public void Pause()
        {
            if (source != null && source.isPlaying)
            {
                pauseTime = source.time;
                source.Pause(); // 使用 Pause 而不是 Stop
                isPaused = true;
            }
        }

        public void Resume()
        {
            if (source != null && source.clip != null && isPaused)
            {
                // UnPause 会从暂停点继续，不需要手动 SetTime
                // 除非我们在暂停期间修改了 Time (由 Time set 属性处理)
                source.UnPause();
                isPaused = false;
            }
        }

        public void Stop()
        {
            if (source != null)
            {
                source.Stop();
                // source.clip = null; // 可选：不一定要清空 Clip，方便下次直接 Play
                isPaused = false;
                source.time = 0f;
            }
        }
    }
}