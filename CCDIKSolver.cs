using UnityEngine;

namespace CharaAnime
{
    public class CCDIKSolver
    {
        // 配置参数
        public Transform ikBone;    // IK 目标 (例如: 左脚IK)

        public Transform target;    // 实际要到达的目标点 (VMD 驱动的点)
        public int iterations = 40; // 迭代次数
        public float controll_weight = 0.5f; // 每次迭代的旋转权重
        public Transform[] chains;  // 关节链: [0]=膝盖, [1]=大腿
        public float minDelta = 0.001f; // 误差阈值
        public bool useLeg = true;  // 是否启用腿部专用逻辑

        // 运行时状态
        public Quaternion[] lastFrameQ;

        public float lastFrameWeight = 0.9f;
        public float minKneeRot = 0.5f;

        public void Solve()
        {
            if (useLeg) SolveLeg();
            else SolveNormal();
        }

        public void SolveLeg()
        {
            // 1. 平滑上一帧
            if (lastFrameQ != null)
            {
                for (int i = 0; i < chains.Length; i++)
                {
                    chains[i].localRotation = Quaternion.Slerp(chains[i].localRotation, lastFrameQ[i], lastFrameWeight);
                }
            }
            else
            {
                lastFrameQ = new Quaternion[chains.Length];
            }

            // 2. CCD 迭代
            for (int j = 0; j < iterations; j++)
            {
                for (int k = 0; k < chains.Length; k++)
                {
                    Transform joint = chains[k];
                    Vector3 jointPos = joint.position;
                    Vector3 toTarget = target.position - jointPos;
                    Vector3 toEffector = ikBone.position - jointPos;

                    // 计算旋转轴和角度
                    float angle = Vector3.Angle(toEffector, toTarget);
                    Vector3 axis = Vector3.Cross(toEffector, toTarget).normalized;

                    if (angle < 0.001f) continue;

                    // 限制单次旋转角度 (防止抽搐)
                    float maxStep = 4f * controll_weight * (k + 1);
                    if (angle > maxStep) angle = maxStep;

                    // 应用旋转
                    Quaternion rot = Quaternion.AngleAxis(angle, axis);
                    joint.rotation = rot * joint.rotation;

                    // 3. 角度限制
                    LimitAngle(joint, k);

                    // 检查是否到达目标
                    if ((target.position - ikBone.position).sqrMagnitude < minDelta)
                    {
                        StoreLastQ();
                        return;
                    }
                }
            }
            StoreLastQ();
        }

        public void SolveNormal()
        {
            // 普通 CCD 逻辑 (用于手臂等)
            // 暂略，因为目前主要修腿，逻辑同 SolveLeg 类似但不含膝盖限制
            SolveLeg(); // 暂时复用
        }

        private void LimitAngle(Transform joint, int index)
        {
            // index 0 是膝盖 (chains[0])，index 1 是大腿
            // MMD 链条顺序通常是：子 -> 父 (所以 0 是膝盖)

            // 膝盖限制
            if (index == 0) // 假设 chains[0] 是膝盖
            {
                // 1. 消除 Y 和 Z 轴旋转 (膝盖只能绕 X 转)
                Vector3 euler = joint.localEulerAngles;
                // 标准化角度到 -180 ~ 180
                if (euler.x > 180) euler.x -= 360;
                if (euler.y > 180) euler.y -= 360;
                if (euler.z > 180) euler.z -= 360;

                // 锁死 YZ
                euler.y = 0;
                euler.z = 0;

                // 2. 限制 X 轴 (只能向后弯曲，不能反关节)
                // MMD 中膝盖弯曲通常是负角度 (或者正角度，取决于坐标系)
                // 假设负是弯曲：限制在 -170 ~ -0.5
                // 你的参考代码里似乎是限制在 minKneeRot ~ 170
                if (euler.x < minKneeRot) euler.x = minKneeRot;
                if (euler.x > 170f) euler.x = 170f;

                joint.localEulerAngles = euler;
            }
        }

        private void StoreLastQ()
        {
            for (int i = 0; i < chains.Length; i++)
                lastFrameQ[i] = chains[i].localRotation;
        }
    }
}