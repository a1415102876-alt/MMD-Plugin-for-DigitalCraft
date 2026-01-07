using UnityEngine;

namespace CharaAnime
{
    /// <summary>
    /// MMD 摄像机控制器，负责播放 VMD 摄像机动画并应用到 Unity 摄像机
    /// </summary>
    public class MmddCameraController : MonoBehaviour
    {
        public MmddCameraController(IntPtr ptr) : base(ptr)
        {
        }

        // =========================================================================
        // 配置与状态
        // =========================================================================
        
        public float VmdFrameRate = 60.0f;
        public float OffsetY = 0.0f;
        public Transform FollowTarget;
        
        /// <summary>摄像机是否处于播放状态</summary>
        public bool IsPlaying = false;
        
        /// <summary>是否使用外部管理器的时间</summary>
        public bool UseExternalTime = true;
        
        /// <summary>标记需要强制刷新（GUI 参数调整时使用）</summary>
        public bool isOffsetDirty = false;

        private Camera targetCamera;
        private List<VmdReader.VmdCameraFrame> frames;
        private float currentTime = 0f;
        private int currentIndex = 0;
        private float maxTime = 0f;
        private List<MonoBehaviour> disabledNativeScripts = new List<MonoBehaviour>();

        // 当前帧插值结果缓存
        private Vector3 valPos = Vector3.zero;
        private Vector3 valRot = Vector3.zero;
        private float valDist = 0f;
        private float valFov = 45f;

        // =========================================================================
        // 生命周期与安装
        // =========================================================================

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
            frames = vmdData.CameraFrames.OrderBy(f => f.FrameNo).ToList();
            maxTime = frames.Last().FrameNo;

            if (targetCamera == null) targetCamera = Camera.main;

            currentTime = 0f;
            currentIndex = 0;
            IsPlaying = true;

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
                if (targetCamera != null)
                {
                    var camCtrl = targetCamera.GetComponent("CameraControl") as MonoBehaviour;
                    if (camCtrl != null) camCtrl.enabled = true;
                }
            }
        }

        /// <summary>手动设置播放时间（用于进度条拖动或外部同步）</summary>
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

            if (isOffsetDirty)
            {
                EvaluateAtTime(currentTime);
                ApplyToCamera();
                isOffsetDirty = false;
            }

            if (UseExternalTime)
            {
                ApplyToCamera();
                return;
            }

            VmdFrameRate = MmddGui.Cfg_VmdFrameRate;
            currentTime += Time.deltaTime * VmdFrameRate;

            if (currentTime > maxTime)
            {
                currentTime %= maxTime;
                currentIndex = 0;
            }

            EvaluateAtTime(currentTime);
            ApplyToCamera();
        }

        // =========================================================================
        // 计算逻辑
        // =========================================================================

        /// <summary>计算指定时间点的 VMD 参数（使用贝塞尔曲线插值）</summary>
        private void EvaluateAtTime(float time)
        {
            if (frames.Count == 0) return;

            // 检测时间回溯，重置索引
            if (currentIndex >= frames.Count || time < frames[currentIndex].FrameNo)
            {
                currentIndex = 0;
            }

            // 定位当前时间所在的关键帧区间
            while (currentIndex < frames.Count - 1 && time >= frames[currentIndex + 1].FrameNo)
            {
                currentIndex++;
            }

            var prev = frames[currentIndex];
            var next = (currentIndex < frames.Count - 1) ? frames[currentIndex + 1] : prev;

            float t = 0f;
            float duration = next.FrameNo - prev.FrameNo;
            if (duration > 0.0001f) t = (time - prev.FrameNo) / duration;
            t = Mathf.Clamp01(t);

            // 计算各属性的贝塞尔插值比率
            float t_x = GetBezierRate(prev.Curve, 0, t);
            float t_y = GetBezierRate(prev.Curve, 4, t);
            float t_z = GetBezierRate(prev.Curve, 8, t);
            float t_r = GetBezierRate(prev.Curve, 12, t);
            float t_d = GetBezierRate(prev.Curve, 16, t);
            float t_f = GetBezierRate(prev.Curve, 20, t);

            // 位置插值
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

        /// <summary>将 VMD 参数转换为 Unity 坐标系并应用到摄像机</summary>
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

            // FOV 兼容模式处理
            if (MmddGui.Cfg_EnableFovMod)
            {
                finalFovVMD = Mathf.Clamp(Mathf.Abs(valDist), 1f, 179f);
                finalDistanceVMD = 0f;
            }
            else
            {
                finalFovVMD = valFov;
                finalDistanceVMD = valDist * scale;
            }

            // MMD 到 Unity 坐标系转换：X/Z 取反，Y 旋转 180 度
            Vector3 vmdOffset = new Vector3(-valPos.x, valPos.y, -valPos.z) * scale;
            Quaternion vmdRotation = Quaternion.Euler(-valRot.x, -valRot.y + 180f, -valRot.z);

            Vector3 finalPos;
            Quaternion finalRot;

            // 跟随目标模式
            if (FollowTarget != null)
            {
                Vector3 targetWorldPos = FollowTarget.position;
                
                Vector3 forward = FollowTarget.forward;
                forward.y = 0f;
                Quaternion targetYaw = Quaternion.identity;
                if (forward.sqrMagnitude > 0.001f)
                {
                    targetYaw = Quaternion.LookRotation(forward.normalized, Vector3.up);
                }

                Vector3 worldLookAtPoint = targetWorldPos + (targetYaw * vmdOffset);
                finalRot = targetYaw * vmdRotation;
                Vector3 distOffset = finalRot * new Vector3(0f, 0f, finalDistanceVMD);
                finalPos = worldLookAtPoint + distOffset;
            }
            else
            {
                finalRot = vmdRotation;
                Vector3 distOffset = finalRot * new Vector3(0f, 0f, finalDistanceVMD);
                finalPos = vmdOffset + distOffset;
            }

            finalPos.y += currentOffsetY;

            // 禁用冲突的原生脚本（每 10 帧检查一次）
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

            targetCamera.transform.position = finalPos;
            targetCamera.transform.rotation = finalRot;
            targetCamera.fieldOfView = finalFovVMD;
        }

        // =========================================================================
        // 数学工具
        // =========================================================================

        /// <summary>计算贝塞尔曲线插值比率（VMD 摄像机曲线格式：x1, x2, y1, y2）</summary>
        private float GetBezierRate(byte[] curve, int offset, float t)
        {
            if (curve == null || curve.Length < 24) return t;
            
            float p1x = curve[offset] / 127f;
            float p2x = curve[offset + 1] / 127f;
            float p1y = curve[offset + 2] / 127f;
            float p2y = curve[offset + 3] / 127f;
            
            if (p1x == p1y && p2x == p2y) return t;

            // 牛顿迭代法求解
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