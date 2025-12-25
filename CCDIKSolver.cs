using UnityEngine;

namespace CharaAnime
{
    public class CCDIKSolver
    {
        public Transform ikBone;
        public Transform endEffector;
        public Transform target;
        public Transform[] chains; // [0]=Knee, [1]=Thigh

        public int iterations = 40;
        public float controll_weight = 1.0f;
        public float minDelta = 0.0001f;
        public bool useLeg = false;
        public float minKneeRot = 5f; // 原始代码默认是 5f，不是 0.5f

        /// <summary>
        /// 全局 IK 位置权重（0~1），对应 FinalIK 的 IKPositionWeight。
        /// </summary>
        public float IKPositionWeight = 1.0f;

        /// <summary>
        /// 每节骨骼的权重，长度与 chains 一致。
        /// 如果为 null 或长度不匹配，会在 Solve() 内按 FinalIK 风格自动生成递减权重。
        /// </summary>
        private float[] boneWeights;

        /// <summary>
        /// 腿部默认弯曲平面的法线方向（世界空间），用于引导膝盖弯曲方向。
        /// 由外部（MmddPoseController.AddIKChain）根据大腿/膝盖/脚位置预计算。
        /// 如果为 Vector3.zero，则退回到简单的 X 轴预弯逻辑。
        /// </summary>
        public Vector3 bendNormal = Vector3.zero;

        /// <summary>
        /// 预弯角度（度），用于打破直腿死锁。
        /// </summary>
        public float preBendAngle = 5.0f;

        /// <summary>
        /// IK 旋转权重（参考 CharaAnime 的 ikRotationWeight），用于调整大腿旋转。
        /// 0 = 不调整，1 = 完全调整。
        /// </summary>
        public float ikRotationWeight = 0.0f;

        /// <summary>
        /// 基础逆旋转（参考 vmdlib.py 的 baseInvQ），用于保持膝盖旋转基于初始骨骼方向。
        /// 在 AddIKChain 时计算，用于几何解算的膝盖角度应用。
        /// </summary>
        public Quaternion? baseInvQ = null;

        /// <summary>
        /// 腿部几何参数缓存（用于快速几何解算）
        /// </summary>
        private float legSum = 0f;        // 大腿+小腿总长度
        private float legMul2 = 0f;       // 2 * 大腿长度 * 小腿长度
        private float legSqrSum = 0f;     // 大腿长度² + 小腿长度²
        private Quaternion minKneeQ;      // 最小膝盖旋转（伸直状态）

        private Quaternion[] lastFrameQ;
        public float lastFrameWeight = 0.7f; // 参考 CharaAnime 的默认值 0.9f，改为 0.7f

        public void ResetHistory() { lastFrameQ = null; }

        /// <summary>
        /// 按 FinalIK 的 FadeOutBoneWeights 思路自动生成权重：
        /// 我们的 chains[0] 更靠近末端（膝盖），chains[最后] 更靠近根（大腿），
        /// 所以越靠近根的权重越高。
        /// </summary>
        private void EnsureBoneWeights()
        {
            if (chains == null || chains.Length == 0) return;

            if (boneWeights == null || boneWeights.Length != chains.Length)
            {
                boneWeights = new float[chains.Length];
                if (chains.Length == 1)
                {
                    boneWeights[0] = 1f;
                }
                else
                {
                    int n = chains.Length;

                    // 腿部特殊处理：优先让膝盖多弯，大腿少弯，贴近 MMD / charaanime 的“膝盖主弯曲”策略
                    if (useLeg && n == 2)
                    {
                        // chains[0] = 膝盖, chains[1] = 大腿
                        boneWeights[0] = 1.0f;   // 膝盖权重最大
                        boneWeights[1] = 0.5f;   // 大腿权重次之
                    }
                    else
                    {
                        // 一般 CCD：越靠近根权重越高（与注释一致）
                        for (int i = 0; i < n; i++)
                        {
                            // 例如 2 节链: [0]=0.5 (末端), [1]=1.0 (靠近根)
                            boneWeights[i] = (i + 1) / (float)n;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 初始化腿部几何参数（参考 vmdlib.py 的 MMDIKSolverLeg.Verify）
        /// </summary>
        public void InitializeLegGeometry()
        {
            if (!useLeg || chains == null || chains.Length < 2 || endEffector == null) return;

            Transform knee = chains[0];
            Transform thigh = chains[1];
            Transform foot = endEffector;

            // 计算长度
            float thighLen = (knee.position - thigh.position).magnitude;
            float shinLen = (foot.position - knee.position).magnitude;
            legSum = thighLen + shinLen;
            legMul2 = thighLen * shinLen * 2f;
            legSqrSum = thighLen * thighLen + shinLen * shinLen;

            // 计算 baseInvQ（参考 vmdlib.py）
            Vector3 thigh2knee = (knee.position - thigh.position).normalized;
            Vector3 knee2foot = (foot.position - knee.position).normalized;
            Quaternion baseQ = Quaternion.FromToRotation(thigh2knee, knee2foot);
            baseInvQ = Quaternion.Inverse(baseQ);

            // 最小膝盖旋转（伸直状态）
            minKneeQ = Quaternion.Euler(minKneeRot, 0, 0);
        }

        public void Solve()
        {
            if (chains == null || chains.Length == 0) return;
            if (target == null || endEffector == null) return;

            float globalW = Mathf.Clamp01(IKPositionWeight);
            if (globalW <= 0f) return;

            EnsureBoneWeights();

            // 1. 平滑处理
            if (lastFrameQ == null || lastFrameQ.Length != chains.Length)
            {
                lastFrameQ = new Quaternion[chains.Length];
                for (int i = 0; i < chains.Length; i++) lastFrameQ[i] = chains[i].localRotation;
            }
            if (lastFrameWeight > 0)
            {
                for (int i = 0; i < chains.Length; i++)
                    chains[i].localRotation = Quaternion.Slerp(chains[i].localRotation, lastFrameQ[i], lastFrameWeight);
            }

            // 🟢 [改进] 如果是腿部且几何参数未初始化，先初始化
            if (useLeg && chains.Length >= 2 && legSum == 0f)
            {
                InitializeLegGeometry();
            }

            // 2. CCD 解算（3D 模式，参考 FinalIK 的 IKSolverCCD）
            float sqrMinDelta = minDelta * minDelta;
            Vector3 targetPos = target.position;

            // 🟢 [腿部专用策略] 如果是腿，使用几何解算（参考 vmdlib.py 的 MMDIKSolverLeg）
            if (useLeg && chains.Length == 2)
            {
                Transform thigh = chains[1];
                Transform knee = chains[0];

                // 1. 准备数据
                // 确保几何参数已初始化
                if (legSqrSum <= 0f) InitializeLegGeometry();

                // ⚠️ 修正：直接使用外部定义的 targetPos，不要重新声明
                // Vector3 targetPos = target.position; <--- 删除这行

                Vector3 thighPos = thigh.position;
                Vector3 distVec = targetPos - thighPos;
                float dist = distVec.magnitude;

                // 2. 第一步：计算并设置膝盖角度 (Knee Angle)
                // 使用余弦定理: c^2 = a^2 + b^2 - 2ab * cos(C)
                float cosAngle = (legSqrSum - dist * dist) / legMul2;

                // 限制 cos 范围并计算角度 (0度=伸直，180度=折叠)
                float kneeAngle = 180.0f - Mathf.Acos(Mathf.Clamp(cosAngle, -1f, 1f)) * Mathf.Rad2Deg;

                // 限制膝盖角度 (0.1f ~ 175f)
                kneeAngle = Mathf.Clamp(kneeAngle, 0.1f, 175f);

                // 设置膝盖旋转 (局部 X 轴为旋转轴)
                if (baseInvQ.HasValue)
                    knee.localRotation = Quaternion.Euler(kneeAngle, 0, 0) * baseInvQ.Value;
                else
                    knee.localRotation = Quaternion.Euler(kneeAngle, 0, 0);

                // 3. 第二步：设置大腿朝向 (Thigh Swing)
                // 膝盖弯曲后，计算新的"大腿->脚踝"向量
                Vector3 currentEffectorPos = endEffector.position;
                Vector3 currentDir = (currentEffectorPos - thighPos).normalized;
                Vector3 targetDir = distVec.normalized; // 大腿->目标

                // 将大腿直接旋转，使脚踝对准目标
                if (currentDir.sqrMagnitude > 1e-6f && targetDir.sqrMagnitude > 1e-6f)
                {
                    Quaternion swing = Quaternion.FromToRotation(currentDir, targetDir);
                    thigh.rotation = swing * thigh.rotation;
                }

                // 4. 第三步：处理膝盖朝向 (Pole Vector / Twist)

                Vector3 kneePos = knee.position;
                // 当前的腿部平面法线
                Vector3 currentPlaneNormal = Vector3.Cross(targetDir, (kneePos - thighPos).normalized);

                // 期望的平面法线 (使用父节点的前方作为参考，避免转身时膝盖扭曲)
                Transform rootT = thigh.parent;
                Vector3 hintForward = (rootT != null) ? rootT.forward : Vector3.forward;

                // 计算目标法线
                Vector3 targetPlaneNormal = Vector3.Cross(targetDir, hintForward);

                // 如果腿踢到了正前方(平行)，导致法线为0，改用右方作为参考
                if (targetPlaneNormal.sqrMagnitude < 0.001f)
                {
                    Vector3 hintRight = (rootT != null) ? rootT.right : Vector3.right;
                    targetPlaneNormal = Vector3.Cross(targetDir, hintRight);
                }

                // 应用扭转 (Twist)
                if (currentPlaneNormal.sqrMagnitude > 0.001f && targetPlaneNormal.sqrMagnitude > 0.001f)
                {
                    Quaternion twist = Quaternion.FromToRotation(currentPlaneNormal, targetPlaneNormal);
                    thigh.rotation = twist * thigh.rotation;
                }

                // 🔴 纯几何解算结束，不进行 CCD 迭代
                StoreLastQ();
            }
        }
        
        // 🟢 [原始代码] StoreLastQ 方法
        private void StoreLastQ()
        {
            if (lastFrameQ == null || lastFrameQ.Length != chains.Length)
            {
                lastFrameQ = new Quaternion[chains.Length];
            }
            for (int i = 0; i < chains.Length; i++)
            {
                lastFrameQ[i] = chains[i].localRotation;
            }
        }

        // --- 限制器 (模仿原始代码的 limitter) ---
        private void Limitter(Transform bone)
        {
            string name = bone.name;
            Vector3 euler = bone.localEulerAngles;

            if (name.Contains("足首") || name.Contains("foot"))
            {
                // 🟢 [原始代码] 脚踝：Z 轴设为 0
                euler.z = 0f;
                bone.localRotation = Quaternion.Euler(euler);
            }
            else if (name.Contains("ひざ") || name.Contains("knee") || name.Contains("lowleg"))
            {
                // 🟢 [原始代码] 膝盖：使用 adjust_rot 确保 Y/Z 一致性，X 限制在 minKneeRot 到 170f
                bool flag3 = AdjustRot(euler.y) == AdjustRot(euler.z);
                if (flag3)
                {
                    euler.y = (float)AdjustRot(euler.y);
                    euler.z = (float)AdjustRot(euler.z);
                }
                
                if (euler.x > 180f)
                {
                    euler.x -= 360f;
                }
                
                if (euler.x < minKneeRot)
                {
                    euler.x = minKneeRot; // ✅ 最小是 minKneeRot（5f），不是负值
                }
                else if (euler.x > 170f)
                {
                    euler.x = 170f;
                }
                
                bone.localRotation = Quaternion.Euler(euler);
            }
        }
        
        // 🟢 [原始代码] adjust_rot 方法：确保 Y/Z 轴的一致性
        private int AdjustRot(float n)
        {
            if (Mathf.Abs(n) > Mathf.Abs(180f - n) && Mathf.Abs(360f - n) > Mathf.Abs(180f - n))
            {
                return 180;
            }
            else
            {
                return 0;
            }
        }
    }
}