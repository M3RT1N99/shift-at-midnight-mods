using Il2Cpp;
using UnityEngine;

namespace OsamaBinLaden
{
    /// <summary>
    /// Positively proves the game's own solo mode. Unknown/initialising state fails closed.
    /// </summary>
    internal static class SessionGate
    {
        public static bool TryGetSoloPlayer(out PlayerManager player, out Transform target)
        {
            player = null;
            target = null;

            FusionNetworkManager network = FusionNetworkManager.Instance;
            if (network == null || !network || !network.isActiveAndEnabled || !network.IsSoloMode())
                return false;

            ClientPlayer client = ClientPlayer.Instance;
            if (client == null || !client || client.playerMan == null || !client.playerMan)
                return false;

            player = client.playerMan;
            if (player.dead || !player.gameObject.activeInHierarchy)
            {
                player = null;
                return false;
            }

            target = player.charController != null && player.charController
                ? player.charController.transform
                : player.transform;

            if (target == null || !target)
            {
                player = null;
                target = null;
                return false;
            }

            return true;
        }
    }
}
