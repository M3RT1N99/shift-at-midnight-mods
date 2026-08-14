using System;
using UnityEngine;

namespace OsamaBinLaden
{
    /// <summary>
    /// Picks a spawn point for the local character. Shared by the solo controller and the
    /// multiplayer host so both encounter paths place the character the same way. Takes the
    /// spawn point list as a count/accessor pair instead of a typed array so it stays agnostic
    /// to whichever array wrapper the interop layer hands back for
    /// <c>HuntManager.huntSpawnPoints</c>.
    /// </summary>
    internal static class SpawnPlacement
    {
        /// <summary>
        /// Prefers the farthest Hunt spawn point that is within
        /// [<paramref name="minimumDistanceMeters"/>, <paramref name="maximumDistanceMeters"/>]
        /// of <paramref name="targetPosition"/>. Falls back to a point behind the target,
        /// scaled into the same bounds, if no spawn point qualifies.
        /// </summary>
        public static Vector3 Resolve(
            int spawnPointCount,
            Func<int, Transform> spawnPointAt,
            Vector3 targetPosition,
            Vector3 targetForward,
            float minimumDistanceMeters,
            float maximumDistanceMeters)
        {
            Vector3 selected = default;
            float selectedDistance = float.NegativeInfinity;
            bool found = false;

            for (int index = 0; index < spawnPointCount; index++)
            {
                Transform point = spawnPointAt(index);
                if (point == null || !point || !point.gameObject.scene.IsValid() ||
                    !point.gameObject.scene.isLoaded)
                    continue;

                float distance = Vector3.Distance(point.position, targetPosition);
                if (distance < minimumDistanceMeters ||
                    distance > maximumDistanceMeters ||
                    distance <= selectedDistance)
                    continue;

                selected = point.position;
                selectedDistance = distance;
                found = true;
            }

            if (found) return selected;

            float fallbackDistance = Math.Clamp(maximumDistanceMeters * 0.75f, minimumDistanceMeters, 30f);
            Vector3 backward = -targetForward;
            backward.y = 0f;
            if (backward.sqrMagnitude < 0.01f) backward = Vector3.back;
            backward.Normalize();
            return targetPosition + (backward * fallbackDistance);
        }
    }
}
