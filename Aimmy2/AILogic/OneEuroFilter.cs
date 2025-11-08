using System;

namespace AILogic
{
    // Implementierung des One-Euro-Filters von G. Casiez, N. Roussel und D. Vogel
    // http://www.lifl.fr/~casiez/1euro/
    // Dies ist eine C#-Portierung des Originalcodes.

    public class OneEuroFilter
    {
        private double _freq;
        private double _mincutoff;
        private double _beta;
        private double _dcutoff;

        private LowPassFilter _xFilter;
        private LowPassFilter _dxFilter;

        private double _lastTime;

        private class LowPassFilter
        {
            private double _y, _a, _s;
            private bool _initialized;

            public void SetAlpha(double alpha)
            {
                if (alpha <= 0.0 || alpha > 1.0)
                    throw new ArgumentException("Alpha should be in (0.0, 1.0]");
                _a = alpha;
            }

            public LowPassFilter(double alpha, double initval = 0.0)
            {
                SetAlpha(alpha);
                _y = _s = initval;
                _initialized = false;
            }

            public double Filter(double value)
            {
                double result;
                if (_initialized)
                {
                    result = _a * value + (1.0 - _a) * _s;
                }
                else
                {
                    result = value;
                    _initialized = true;
                }
                _y = value;
                _s = result;
                return result;
            }

            public double FilterWithTime(double value, double dt)
            {
                SetAlpha(Alpha(dt, 1.0 / (2.0 * Math.PI * _a))); // _a wird hier als cutoff Frequenz behandelt
                return Filter(value);
            }

            public bool HasLastRawValue() => _initialized;
            public double LastRawValue() => _y;
        }

        private static double Alpha(double dt, double cutoff)
        {
            double te = 1.0 / (2.0 * Math.PI * cutoff);
            return 1.0 / (1.0 + te / dt);
        }


        public OneEuroFilter(double freq, double mincutoff = 1.0, double beta = 0.0, double dcutoff = 1.0)
        {
            if (freq <= 0) throw new ArgumentException("Frequenz muss positiv sein.");
            if (mincutoff <= 0) throw new ArgumentException("MinCutoff muss positiv sein.");
            if (dcutoff <= 0) throw new ArgumentException("DCutoff muss positiv sein.");

            _freq = freq;
            _mincutoff = mincutoff;
            _beta = beta;
            _dcutoff = dcutoff;

            _xFilter = new LowPassFilter(Alpha(1.0 / _freq, _mincutoff));
            _dxFilter = new LowPassFilter(Alpha(1.0 / _freq, _dcutoff));
            _lastTime = -1.0;
        }

        public double Filter(double value, double timestamp = -1.0)
        {
            // REPARATUR HIER:
            // Verhindert Division durch Null, wenn dt = 0
            if (_lastTime != -1.0 && timestamp != -1.0)
            {
                double dt = timestamp - _lastTime;
                if (dt > 0.0)
                {
                    _freq = 1.0 / dt;
                }
                // ansonsten (wenn dt = 0), behalten wir die alte Frequenz
            }
            _lastTime = timestamp;

            double prev_x = _xFilter.HasLastRawValue() ? _xFilter.LastRawValue() : value;
            double dx = (value - prev_x) * _freq;
            double edx = _dxFilter.Filter(dx);

            double cutoff = _mincutoff + _beta * Math.Abs(edx);
            _xFilter.SetAlpha(Alpha(1.0 / _freq, cutoff));

            return _xFilter.Filter(value);
        }
    }
}