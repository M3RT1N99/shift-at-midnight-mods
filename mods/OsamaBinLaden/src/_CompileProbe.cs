using Il2Cpp;
using Il2CppFusion;
using UnityEngine;

namespace OsamaBinLaden
{
    internal static class CompileProbe
    {
        public static PlayerManager Find(PlayerRef player)
        {
            var network = FusionNetworkManager.Instance;
            NetworkObject playerObject = network?.GetPlayer(player);
            if (playerObject != null)
            {
                PlayerManager direct = playerObject.GetComponent<PlayerManager>();
                if (direct != null) return direct;
                PlayerManager child = playerObject.GetComponentInChildren<PlayerManager>(true);
                if (child != null) return child;
            }

            StoreManager store = StoreManager.Instance;
            var players = store?.playerMans;
            if (players == null) return null;
            for (int index = 0; index < players.Count; index++)
            {
                PlayerManager candidate = players[index];
                if (candidate != null && candidate.Object != null &&
                    candidate.Object.InputAuthority.PlayerId == player.PlayerId)
                    return candidate;
            }
            return null;
        }
    }
}
