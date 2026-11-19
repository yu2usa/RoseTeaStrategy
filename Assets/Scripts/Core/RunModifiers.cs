namespace RogueRTS
{
    /// <summary>
    /// 陣営全体にかかる能力補正。自軍は報酬で、敵軍はステージ進行で強くなる。
    /// </summary>
    public class RunModifiers
    {
        public float damageMul = 1f;
        public float hpMul = 1f;
        public float rangeMul = 1f;
        public float attackSpeedMul = 1f;
        public float moveSpeedMul = 1f;
        public float buildSpeedMul = 1f;
    }
}
