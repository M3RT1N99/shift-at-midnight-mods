namespace OsamaBinLaden.Multiplayer
{
    /// <summary>
    /// Per-sender freshness check for <see cref="EncounterProtocol"/> traffic. This is not
    /// authentication (Fusion's own sender identity already proves who sent a frame); it only
    /// rejects duplicate or replayed frames and frames from a generation the sender was never
    /// issued. Has no Unity, MelonLoader or Fusion dependency so it can be smoke-tested.
    /// </summary>
    internal sealed class SequenceGuard
    {
        private bool _hasAccepted;
        private ulong _epoch;
        private ulong _lastSequence;

        /// <summary>
        /// True and advances state if <paramref name="epoch"/> matches (or first-establishes)
        /// the tracked generation and <paramref name="sequence"/> is strictly greater than the
        /// last accepted value for that generation. A new, higher epoch always resets the
        /// sequence floor, since it represents a fresh handshake generation.
        /// </summary>
        public bool TryAccept(ulong epoch, ulong sequence)
        {
            if (sequence == 0) return false;

            if (!_hasAccepted)
            {
                _hasAccepted = true;
                _epoch = epoch;
                _lastSequence = sequence;
                return true;
            }

            if (epoch < _epoch) return false;
            if (epoch > _epoch)
            {
                _epoch = epoch;
                _lastSequence = sequence;
                return true;
            }

            if (sequence <= _lastSequence) return false;
            _lastSequence = sequence;
            return true;
        }

        public void Reset()
        {
            _hasAccepted = false;
            _epoch = 0;
            _lastSequence = 0;
        }
    }
}
