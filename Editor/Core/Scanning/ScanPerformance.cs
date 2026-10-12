using System;

namespace ApexForge.ProjectOptimizer
{
	/// <summary>Hardware-aware limits for background, non-Unity scan work.</summary>
	public static class ScanPerformance
	{
		public static int EffectiveWorkerCount(OptimizerSettings settings) => MaximumWorkerCount(settings);

		public static int EffectiveFrameBudgetMs(OptimizerSettings settings)
		{
			int value = settings != null ? settings.scanFrameBudgetMs : 50;
			if (value <= 0) value = 50;
			if (settings == null) return value;
			switch (settings.scanPacingMode)
			{
				case ScanPacingMode.Interactive: value = Math.Min(value, 8); break;
				case ScanPacingMode.Balanced: value = Math.Min(value, 30); break;
				case ScanPacingMode.Throughput: value = Math.Min(100, Math.Max(value, 50)); break;
				case ScanPacingMode.Background: value = Math.Min(value, 16); break;
			}
			return Math.Max(1, Math.Min(100, value));
		}

		public static int EffectiveBatchSize(OptimizerSettings settings)
		{
			int value = settings != null ? settings.scanBatchSize : 256;
			if (value <= 0) value = 256;
			if (settings != null)
			{
				switch (settings.scanPacingMode)
				{
					case ScanPacingMode.Interactive: value = Math.Min(value, 64); break;
					case ScanPacingMode.Balanced: value = Math.Min(value, 256); break;
					case ScanPacingMode.Throughput: value = Math.Min(4096, Math.Max(value, 512)); break;
					case ScanPacingMode.Background: value = Math.Min(value, 128); break;
				}
			}
			return Math.Max(1, Math.Min(4096, value));
		}

		public static int EffectiveQueueCapacity(OptimizerSettings settings, int workerCount)
		{
			int requested = settings != null ? settings.scanQueueCapacity : 0;
			int automatic = Math.Max(128, Math.Min(4096, Math.Max(1, workerCount) * 24));
			return Math.Max(64, Math.Min(8192, requested > 0 ? requested : automatic));
		}
		public static int MaximumWorkerCount(OptimizerSettings settings)
		{
			int requested = settings != null ? settings.scanWorkerCount : 0;
			if (requested > 0) return Math.Max(1, Math.Min(32, requested));

			int logical = Math.Max(1, Environment.ProcessorCount);
			int reserved = settings != null && settings.scanPacingMode == ScanPacingMode.Throughput ? 1 : 2;
			return Math.Max(2, Math.Min(16, logical - reserved));
		}

		public static int InitialWorkerTarget(OptimizerSettings settings, int maximum)
		{
			maximum = Math.Max(1, maximum);
			if (settings == null || settings.scanWorkerCount > 0) return maximum;
			switch (settings.scanPacingMode)
			{
				case ScanPacingMode.Interactive: return Math.Max(1, (maximum + 1) / 2);
				case ScanPacingMode.Background: return Math.Max(1, (maximum * 3 + 3) / 4);
				case ScanPacingMode.Throughput: return maximum;
				default: return Math.Max(1, (maximum * 3 + 3) / 4);
			}
		}

		public sealed class AdaptiveFrameBudgetController
		{
			readonly int m_Minimum;
			readonly int m_Maximum;
			double m_Current;
			double m_ExponentialAverage;
			bool m_HasSample;

			public int CurrentMilliseconds => (int)Math.Round(m_Current);

			public AdaptiveFrameBudgetController(OptimizerSettings settings)
			{
				m_Maximum = EffectiveFrameBudgetMs(settings);
				int minimum = settings != null && settings.scanPacingMode == ScanPacingMode.Interactive ? 2 : 4;
				m_Minimum = Math.Min(minimum, m_Maximum);
				m_Current = m_Maximum;
			}

			public void Observe(double sliceMilliseconds)
			{
				if (sliceMilliseconds <= 0.0) return;
				m_ExponentialAverage = m_HasSample ? m_ExponentialAverage * 0.80 + sliceMilliseconds * 0.20 : sliceMilliseconds;
				m_HasSample = true;
				if (m_ExponentialAverage > m_Current * 1.15) m_Current = Math.Max(m_Minimum, m_Current * 0.90);
				else if (m_ExponentialAverage < m_Current * 0.70) m_Current = Math.Min(m_Maximum, m_Current * 1.05);
			}
		}
	}
}
