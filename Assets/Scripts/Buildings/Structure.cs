using UnityEngine;

namespace RogueRTS
{
    /// <summary>グリッドの1マスを占める防壁または門。上にタレットを1基載せられる。</summary>
    public class Structure : MonoBehaviour
    {
        public BuildingType type;
        [Tooltip("タレットを載せる位置")]
        public Transform turretMount;

        /// <summary>載っているタレット。なければ null。</summary>
        public Turret Turret { get; set; }
    }
}
