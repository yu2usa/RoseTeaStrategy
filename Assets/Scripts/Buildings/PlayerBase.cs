using System.Collections.Generic;
using UnityEngine;

namespace RogueRTS
{
    /// <summary>自軍の拠点。ユニットを生産する。破壊されると敗北。</summary>
    [RequireComponent(typeof(Health))]
    public class PlayerBase : MonoBehaviour
    {
        public ResourceBank bank;
        [Tooltip("生産できるユニットのプレハブ")]
        public Unit[] producibleUnits;
        public Transform spawnPoint;
        [Tooltip("生産されたユニットが向かう集合地点")]
        public Transform rallyPoint;
        public int maxQueue = 5;

        public Health Health { get; private set; }

        readonly Queue<Unit> queue = new Queue<Unit>();
        float progress;

        public int QueueCount => queue.Count;
        public Unit CurrentItem => queue.Count > 0 ? queue.Peek() : null;
        public float Progress01 => queue.Count > 0 ? Mathf.Clamp01(progress / queue.Peek().data.buildTime) : 0f;

        void Awake() => Health = GetComponent<Health>();

        public bool CanQueue(int index)
        {
            return queue.Count < maxQueue && bank.Gold >= producibleUnits[index].data.cost;
        }

        /// <summary>ゴールドを支払って生産キューに積む。</summary>
        public bool TryQueue(int index)
        {
            if (!CanQueue(index)) return false;

            Unit prefab = producibleUnits[index];
            bank.TrySpendGold(prefab.data.cost);
            queue.Enqueue(prefab);
            return true;
        }

        void Update()
        {
            if (queue.Count == 0) return;

            progress += Time.deltaTime * GameManager.Instance.ModsFor(Team.Player).buildSpeedMul;
            if (progress < queue.Peek().data.buildTime) return;

            progress = 0f;
            SpawnUnit(queue.Dequeue());
        }

        /// <summary>拠点前にユニットを出現させ、集合地点へ向かわせる。</summary>
        public Unit SpawnUnit(Unit prefab)
        {
            Vector3 position = NavMeshUtil.Snap(spawnPoint.position + NavMeshUtil.Scatter(1.5f), 4f);
            Unit unit = Instantiate(prefab, position, spawnPoint.rotation);
            unit.MoveTo(NavMeshUtil.Snap(rallyPoint.position + NavMeshUtil.Scatter(2.5f), 4f));
            return unit;
        }
    }
}
