using UnityEngine;
using UnityEngine.SceneManagement;

namespace Puckmite.Game
{
    /// <summary>
    /// Scene names and transitions in one place, plus the campaign instance that must survive them.
    /// Later screens (title, ending) slot in here as new scene names and Load methods.
    /// </summary>
    public static class GameFlow
    {
        public const string BattleScene = "Battle";
        public const string ShopScene = "Shop";

        private static CampaignState _campaign;

        /// <summary>The running campaign, created fresh on first use — so pressing Play directly in either
        /// scene starts a new campaign there, which is what editor testing wants.</summary>
        public static CampaignState Campaign => _campaign ?? (_campaign = new CampaignState());

        public static void LoadBattle()
        {
            SceneManager.LoadScene(BattleScene);
        }

        public static void LoadShop()
        {
            SceneManager.LoadScene(ShopScene);
        }

        // With domain reload off (Enter Play Mode Options) statics survive between plays; this keeps every
        // play session starting on a fresh campaign either way.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay()
        {
            _campaign = null;
        }
    }
}
