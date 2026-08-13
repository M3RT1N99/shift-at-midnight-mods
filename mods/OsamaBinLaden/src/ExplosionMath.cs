using System;

namespace OsamaBinLaden
{
    internal static class ExplosionMath
    {
        /// <summary>
        /// Full damage inside the trigger distance, then linear falloff to zero at radius.
        /// Invalid values fail harmlessly.
        /// </summary>
        public static float CalculateDamage(
            float distance,
            float triggerDistance,
            float radius,
            float maximumDamage)
        {
            if (!float.IsFinite(distance) || !float.IsFinite(triggerDistance) ||
                !float.IsFinite(radius) || !float.IsFinite(maximumDamage) ||
                distance < 0f || radius <= 0f || maximumDamage <= 0f || distance >= radius)
                return 0f;

            triggerDistance = Math.Clamp(triggerDistance, 0f, radius);
            if (distance <= triggerDistance || triggerDistance >= radius)
                return maximumDamage;

            float falloff = 1f - ((distance - triggerDistance) / (radius - triggerDistance));
            return Math.Clamp(maximumDamage * falloff, 0f, maximumDamage);
        }
    }
}
