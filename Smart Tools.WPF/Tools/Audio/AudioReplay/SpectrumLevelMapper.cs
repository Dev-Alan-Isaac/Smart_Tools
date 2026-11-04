namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Turns an FFT magnitude spectrum into bar heights: bins grouped into log-spaced bands (bass on the left, treble on
    /// the right), auto-gained to the track's own loudness, then a square-root curve so quiet bands still move.
    /// Shared by the Music page (AudioReplayManager) and the Media Viewer's music view, so both bars move the same way.
    /// Pure maths, no WPF types.
    /// </summary>
    public sealed class SpectrumLevelMapper
    {
        private const float MinCeiling = 1e-6f;
        private const double MinFrequency = 30.0;
        private const double MaxFrequency = 16000.0;

        private float _ceiling = MinCeiling;

        /// <summary>Forget the loudness learned from the previous track.</summary>
        public void Reset() => _ceiling = MinCeiling;

        /// <summary>
        /// Fills <paramref name="levels"/> (one slot per bar) with 0..1 values from <paramref name="magnitudes"/>
        /// (FFT of <paramref name="fftLength"/> points at <paramref name="sampleRate"/>).
        /// </summary>
        public void Fill(float[] magnitudes, int fftLength, int sampleRate, float[] levels)
        {
            int barCount = levels.Length;
            double maxFreq = Math.Min(sampleRate / 2.0, MaxFrequency);
            double logMin = Math.Log10(MinFrequency);
            double logMax = Math.Log10(maxFreq);
            float frameMax = 0f;

            for (int b = 0; b < barCount; b++)
            {
                double loFreq = Math.Pow(10, logMin + (logMax - logMin) * b / barCount);
                double hiFreq = Math.Pow(10, logMin + (logMax - logMin) * (b + 1) / barCount);

                int loBin = Math.Max(1, (int)(loFreq * fftLength / sampleRate));
                int hiBin = Math.Min(magnitudes.Length - 1, (int)(hiFreq * fftLength / sampleRate));
                if (hiBin < loBin) hiBin = loBin;

                float maxMag = 0f;
                for (int i = loBin; i <= hiBin; i++)
                    if (magnitudes[i] > maxMag) maxMag = magnitudes[i];

                levels[b] = maxMag;
                if (maxMag > frameMax) frameMax = maxMag;
            }

            // Auto-gain: snap up to a louder peak instantly, decay slowly (~halves every ~4 s at a 30 ms tick) through quiet passages.
            _ceiling = frameMax > _ceiling ? frameMax : Math.Max(_ceiling * 0.995f, MinCeiling);

            for (int b = 0; b < barCount; b++)
                levels[b] = (float)Math.Sqrt(Math.Clamp(levels[b] / _ceiling, 0f, 1f));
        }
    }
}
