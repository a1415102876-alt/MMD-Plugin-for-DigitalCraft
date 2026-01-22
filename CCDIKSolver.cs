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

            // ----------------------------------------------------------------------
            // 策略：vmdlib.py 风格混合解算 (Analytic Knee + Aiming Thigh)
            // ----------------------------------------------------------------------
            if (useLeg && chains.Length == 2)
            {
                // 1. 初始化变量
                // 注意：这里需要你原本类里的 legSqrSum 变量，如果没有初始化逻辑，
                // 请确保 Start() 或 InitializeLegGeometry() 被调用过
                if (legMul2 == 0f) InitializeLegGeometry();

                Transform thigh = chains[1]; // 大腿
                Transform knee = chains[0];  // 小腿

                Vector3 targetPos = target.position;
                Vector3 thighPos = thigh.position;
                float dist = Vector3.Distance(targetPos, thighPos);

                // 2. 处理膝盖 (Knee) - 解析法
                // -----------------------------------------------------------
                // 能够着就弯，够不着就直
                float maxReach = Mathf.Sqrt(legSqrSum + legMul2);
                if (dist < maxReach)
                {
                    // 在射程内：使用余弦定理计算精确角度
                    float cosAngle = (legSqrSum - dist * dist) / legMul2;
                    cosAngle = Mathf.Clamp(cosAngle, -1.0f, 1.0f);

                    // 计算角度 (180度 - 算出的内角)
                    float kneeAngle = 180.0f - Mathf.Acos(cosAngle) * Mathf.Rad2Deg;

                    // 限制角度 (使用你原本定义的变量 minKneeRot)
                    kneeAngle = Mathf.Clamp(kneeAngle, minKneeRot, 175f);

                    // 应用旋转
                    if (baseInvQ.HasValue)
                        knee.localRotation = Quaternion.Euler(kneeAngle, 0, 0) * baseInvQ.Value;
                    else
                        knee.localRotation = Quaternion.Euler(kneeAngle, 0, 0);
                }
                else
                {
                    // [关键] 射程外（过伸）：直接锁定为直腿
                    float straightAngle = minKneeRot;

                    if (baseInvQ.HasValue)
                        knee.localRotation = Quaternion.Euler(straightAngle, 0, 0) * baseInvQ.Value;
                    else
                        knee.localRotation = Quaternion.Euler(straightAngle, 0, 0);
                }

                // 3. 处理大腿 (Thigh) - 指向法 (Aiming)
                // -----------------------------------------------------------
                // 简单迭代 2 次，把脚对准目标
                for (int i = 0; i < 2; i++)
                {
                    Vector3 currentEffectorPos = endEffector.position;
                    Vector3 toEffector = currentEffectorPos - thighPos;
                    Vector3 toTarget = targetPos - thighPos;

                    // 避免除零
                    if (toEffector.sqrMagnitude < 1e-4f || toTarget.sqrMagnitude < 1e-4f) break;

                    // 计算旋转差
                    Quaternion rot = Quaternion.FromToRotation(toEffector, toTarget);

                    // 应用旋转到大腿
                    thigh.rotation = rot * thigh.rotation;
                }

                StoreLastQ();
            }
            else
            {
                // 非腿部 IK (手臂等)，保留原有的 CCD 逻辑
                // 🔴 修正点：使用 'iterations' 而不是 'max_iterations'
                for (int i = 0; i < iterations; i++)
                {
                    Iterate(i); // 这里调用下面补充的 Iterate 方法
                }
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

            if (name.Contains("ひざ") || name.Contains("knee") || name.Contains("lowleg"))
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
        public void Iterate(int iteration)
        {
            // 标准 CCD 算法实现
            for (int i = 0; i < chains.Length; i++)
            {
                // 跳过末端效应器本身，因为它不需要旋转自己去够目标
                if (chains[i] == endEffector) continue;

                Transform bone = chains[i];
                Vector3 toEnd = endEffector.position - bone.position;
                Vector3 toTarget = target.position - bone.position;

                // 仅当两者都有长度时才计算
                if (toEnd.sqrMagnitude > minDelta && toTarget.sqrMagnitude > minDelta)
                {
                    // 计算旋转：从“指向末端”转到“指向目标”
                    Quaternion r = Quaternion.FromToRotation(toEnd, toTarget);
                    bone.rotation = r * bone.rotation;

                    // 这里可以添加关节限制逻辑(Limits)如果原本有的话...
                    // 但对于通用 CCD，这样是最基础的
                }
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