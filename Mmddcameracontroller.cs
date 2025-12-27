using UnityEngine;

namespace CharaAnime
{
    public class MmddCameraController : MonoBehaviour
    {
        public MmddCameraController(IntPtr ptr) : base(ptr)
        {
        }

        // -------------------------------------------------------------------------
        // 1. 配置与状态变量
        // -------------------------------------------------------------------------
        public float VmdFrameRate = 60.0f;

        public float OffsetY = 0.0f;
        public Transform FollowTarget;

        /// 摄像机是否处于由脚本接管的播放状态
        public bool IsPlaying = false;


        /// 是否使用外部管理器的时间（True）或内部自增时间（False）

        public bool UseExternalTime = true;

        // 标记需强制刷新（配合 GUI 调整参数使用）
        public bool isOffsetDirty = false;

        // 内部运行时数据
        private Camera targetCamera;

        private List<VmdReader.VmdCameraFrame> frames;
        private float currentTime = 0f;
        private int currentIndex = 0;
        private float maxTime = 0f;

        // 被禁用的原生脚本缓存（用于停止播放时恢复）
        private List<MonoBehaviour> disabledNativeScripts = new List<MonoBehaviour>();

        // 当前帧计算出的 MMD 原始参数缓存
        private Vector3 valPos = Vector3.zero;

        private Vector3 valRot = Vector3.zero;
        private float valDist = 0f;
        private float valFov = 45f;

        // -------------------------------------------------------------------------
        // 2. 生命周期与安装
        // -------------------------------------------------------------------------

        public static MmddCameraController Install(GameObject container)
        {
            try
            {
                var ctrl = container.AddComponent<MmddCameraController>();
                if (ctrl != null)
                {
                    ctrl.targetCamera = Camera.main;
                }
                return ctrl;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[MmddCamera] Install exception: {e.Message}");
                return null;
            }
        }

        public void Play(VmdReader.VmdData vmdData)
        {
            if (vmdData == null || vmdData.CameraFrames.Count == 0)
            {
                Debug.LogWarning("[MmddCamera] No camera frames found.");
                return;
            }

            disabledNativeScripts.Clear();

            // 确保关键帧按时间顺序排列
            frames = vmdData.CameraFrames.OrderBy(f => f.FrameNo).ToList();
            maxTime = frames.Last().FrameNo;

            if (targetCamera == null) targetCamera = Camera.main;

            // 初始化状态
            currentTime = 0f;
            currentIndex = 0;
            IsPlaying = true;

            // 立即应用第一帧
            EvaluateAtTime(0f);
            ApplyToCamera();
        }

        public void Stop()
        {
            IsPlaying = false;
        }

        public void RestoreControl()
        {
            IsPlaying = false;

            // 恢复被禁用的原生摄像机脚本
            if (disabledNativeScripts.Count > 0)
            {
                foreach (var script in disabledNativeScripts)
                {
                    if (script != null) script.enabled = true;
                }
                disabledNativeScripts.Clear();
            }
            else
            {
                // 兜底恢复：尝试寻找常见的 CameraControl 脚本
                if (targetCamera != null)
                {
                    var camCtrl = targetCamera.GetComponent("CameraControl") as MonoBehaviour;
                    if (camCtrl != null) camCtrl.enabled = true;
                }
            }
        }

        /// 手动设置时间（用于进度条拖动或外部同步）
        public void SetTime(float time)
        {
            if (frames == null || frames.Count == 0) return;
            currentTime = time;
            EvaluateAtTime(currentTime);
            ApplyToCamera();
        }

        private void LateUpdate()
        {
            if (frames == null || targetCamera == null || !IsPlaying) return;

            // 1. 响应强制刷新请求（如参数变动）
            if (isOffsetDirty)
            {
                EvaluateAtTime(currentTime);
                ApplyToCamera();
                isOffsetDirty = false;
            }

            // 2. 外部时间模式（由 Manager 控制进度）
            if (UseExternalTime)
            {
                // 持续应用以覆盖原生脚本的 LateUpdate
                ApplyToCamera();
                return;
            }

            // 3. 内部自增时间模式
            VmdFrameRate = MmddGui.Cfg_VmdFrameRate;
            currentTime += Time.deltaTime * VmdFrameRate;

            // 循环处理
            if (currentTime > maxTime)
            {
                currentTime %= maxTime;
                currentIndex = 0;
            }

            EvaluateAtTime(currentTime);
            ApplyToCamera();
        }

        // -------------------------------------------------------------------------
        // 3.计算逻辑
        // -------------------------------------------------------------------------

        /// 计算当前时间点的 VMD 参数（贝塞尔插值）
        private void EvaluateAtTime(float time)
        {
            if (frames.Count == 0) return;

            // 检测时间回溯（如进度条回拖），重置索引
            if (currentIndex >= frames.Count || time < frames[currentIndex].FrameNo)
            {
                currentIndex = 0;
            }

            // 寻找当前时间所在的关键帧区间
            while (currentIndex < frames.Count - 1 && time >= frames[currentIndex + 1].FrameNo)
            {
                currentIndex++;
            }

            var prev = frames[currentIndex];
            var next = (currentIndex < frames.Count - 1) ? frames[currentIndex + 1] : prev;

            // 计算时间进度 t (0~1)
            float t = 0f;
            float duration = next.FrameNo - prev.FrameNo;
            if (duration > 0.0001f) t = (time - prev.FrameNo) / duration;
            t = Mathf.Clamp01(t);

            // 获取各属性的贝塞尔曲线插值比率
            float t_x = GetBezierRate(prev.Curve, 0, t);
            float t_y = GetBezierRate(prev.Curve, 4, t);
            float t_z = GetBezierRate(prev.Curve, 8, t);
            float t_r = GetBezierRate(prev.Curve, 12, t);
            float t_d = GetBezierRate(prev.Curve, 16, t);
            float t_f = GetBezierRate(prev.Curve, 20, t);

            // 计算插值结果并缓存
            valPos.x = Mathf.Lerp(prev.Position.x, next.Position.x, t_x);
            valPos.y = Mathf.Lerp(prev.Position.y, next.Position.y, t_y);
            valPos.z = Mathf.Lerp(prev.Position.z, next.Position.z, t_z);

            // 旋转插值
            Quaternion prevRot = Quaternion.Euler(prev.Rotation.x, prev.Rotation.y, prev.Rotation.z);
            Quaternion nextRot = Quaternion.Euler(next.Rotation.x, next.Rotation.y, next.Rotation.z);
            valRot = Quaternion.Slerp(prevRot, nextRot, t_r).eulerAngles;

            valDist = Mathf.Lerp(prev.Distance, next.Distance, t_d);
            valFov = Mathf.Lerp(prev.Fov, next.Fov, t_f);
        }

        /// 将计算好的 VMD 参数转换为 Unity 坐标并应用给摄像机
        private void ApplyToCamera()
        {
            if (targetCamera == null)
            {
                targetCamera = Camera.main;
                if (targetCamera == null) return;
            }

            float scale = MmddGui.Cfg_CameraScale;
            float currentOffsetY = MmddGui.Cfg_CameraOffsetY;

            float finalDistanceVMD;
            float finalFovVMD;

            // --- 处理 FOV 兼容模式 ---
            if (MmddGui.Cfg_EnableFovMod)
            {
                // Kimochi's FOV Mod 模式：利用 Distance 通道存储 FOV 数据
                finalFovVMD = Mathf.Clamp(Mathf.Abs(valDist), 1f, 179f);
                finalDistanceVMD = 0f; // 强制距离归零（贴脸模式）
            }
            else
            {
                // 标准模式
                finalFovVMD = valFov;
                // VMD 距离为负值，直接缩放保留负号，配合后续旋转实现后退
                finalDistanceVMD = valDist * scale;
            }

            // --- 坐标系转换
            // 位置：X, Z 取反
            Vector3 vmdOffset = new Vector3(-valPos.x, valPos.y, -valPos.z) * scale;
            // 旋转：X, Z 取反，Y 取反并旋转 180 度以对齐相机后方
            Quaternion vmdRotation = Quaternion.Euler(-valRot.x, -valRot.y + 180f, -valRot.z);

            Vector3 finalPos;
            Quaternion finalRot;

            // --- 实时跟随 FollowTarget ---
            if (FollowTarget != null)
            {
                // 实时获取目标位置和朝向
                Vector3 targetWorldPos = FollowTarget.position;
                
                // 计算 Y 轴朝向（只取水平旋转）
                Vector3 forward = FollowTarget.forward;
                forward.y = 0f;
                Quaternion targetYaw = Quaternion.identity;
                if (forward.sqrMagnitude > 0.001f)
                {
                    targetYaw = Quaternion.LookRotation(forward.normalized, Vector3.up);
                }

                // 1. 计算看向的目标点（基于目标位置 + VMD偏移）
                Vector3 worldLookAtPoint = targetWorldPos + (targetYaw * vmdOffset);

                // 2. 计算最终旋转 (目标朝向 + VMD Rotation)
                finalRot = targetYaw * vmdRotation;

                // 3. 应用距离，沿最终朝向后退
                Vector3 distOffset = finalRot * new Vector3(0f, 0f, finalDistanceVMD);

                finalPos = worldLookAtPoint + distOffset;
            }
            else
            {
                // 无目标模式（绝对坐标）
                finalRot = vmdRotation;
                Vector3 distOffset = finalRot * new Vector3(0f, 0f, finalDistanceVMD);
                finalPos = vmdOffset + distOffset;
            }

            // 应用额外的高度偏移
            finalPos.y += currentOffsetY;

            // --- 冲突脚本禁用逻辑 
            if (Time.frameCount % 10 == 0)
            {
                foreach (var comp in targetCamera.gameObject.GetComponents<MonoBehaviour>())
                {
                    if (comp == null || comp == this) continue;

                    var type = comp.GetIl2CppType();
                    if (type == null) continue;

                    string name = type.Name;
                    if (name.Contains("Mmdd")) continue;
                    if (disabledNativeScripts.Contains(comp)) continue;

                    // 禁用常见的抢夺相机控制权的脚本
                    if (name.Contains("CameraControl") || name.Contains("LookAt") ||
                        name.Contains("Amplify") || name.Contains("Universal") ||
                        name.Contains("CameraStack") || name.Contains("Orbit"))
                    {
                        if (comp.enabled)
                        {
                            comp.enabled = false;
                            disabledNativeScripts.Add(comp);
                        }
                    }
                }
            }

            // 应用最终变换
            targetCamera.transform.position = finalPos;
            targetCamera.transform.rotation = finalRot;
            targetCamera.fieldOfView = finalFovVMD;
        }

        // -------------------------------------------------------------------------
        // 4. 数学工具 (贝塞尔插值)
        // -------------------------------------------------------------------------

        private float GetBezierRate(byte[] curve, int offset, float t)
        {
            if (curve == null || curve.Length < 24) return t;
            
            // VMD 摄像机曲线的字节顺序是 (x1, x2, y1, y2)，不是 (x1, y1, x2, y2)！
            // 这与骨骼曲线的格式不同
            float p1x = curve[offset] / 127f;      // 第一个控制点 X
            float p2x = curve[offset + 1] / 127f;  // 第二个控制点 X
            float p1y = curve[offset + 2] / 127f;  // 第一个控制点 Y
            float p2y = curve[offset + 3] / 127f;  // 第二个控制点 Y
            
            // 检查是否为线性插值（控制点在对角线上）
            if (p1x == p1y && p2x == p2y) return t;

            // 牛顿迭代法求解 x(t) = time 对应的 t 值
            float t_value = t;
            for (int i = 0; i < 8; i++)
            {
                float x_est = SampleBezier(p1x, p2x, t_value);
                float slope = SampleBezierDerivative(p1x, p2x, t_value);
                if (slope == 0) break;
                t_value -= (x_est - t) / slope;
            }
            t_value = Mathf.Clamp01(t_value);
            return SampleBezier(p1y, p2y, t_value);
        }

        private float SampleBezier(float p1, float p2, float t)
        {
            float oneMinusT = 1f - t;
            return 3f * oneMinusT * oneMinusT * t * p1 + 3f * oneMinusT * t * t * p2 + t * t * t;
        }

        private float SampleBezierDerivative(float p1, float p2, float t)
        {
            float oneMinusT = 1f - t;
            return 3f * oneMinusT * oneMinusT * p1 + 6f * oneMinusT * t * (p2 - p1) + 3f * t * t;
        }
    }
}