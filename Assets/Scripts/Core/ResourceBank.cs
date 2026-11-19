using UnityEngine;

namespace RogueRTS
{
    /// <summary>
    /// 資源の所持量を管理する。
    /// ゴールドは時間で増えてユニット生産に使い、コインは敵の撃破で増えて建築に使う。
    /// </summary>
    public class ResourceBank : MonoBehaviour
    {
        public int startingGold = 150;
        public float incomePerSecond = 5f;
        public int startingCoins = 0;

        public int Gold { get; private set; }
        public int Coins { get; private set; }

        float incomeFraction;

        void Awake()
        {
            Gold = startingGold;
            Coins = startingCoins;
        }

        void Update()
        {
            incomeFraction += incomePerSecond * Time.deltaTime;
            if (incomeFraction < 1f) return;

            int whole = Mathf.FloorToInt(incomeFraction);
            incomeFraction -= whole;
            Gold += whole;
        }

        public void AddGold(int amount) => Gold += amount;

        public bool TrySpendGold(int amount)
        {
            if (Gold < amount) return false;
            Gold -= amount;
            return true;
        }

        public void AddCoins(int amount) => Coins += amount;

        public bool TrySpendCoins(int amount)
        {
            if (Coins < amount) return false;
            Coins -= amount;
            return true;
        }
    }
}
