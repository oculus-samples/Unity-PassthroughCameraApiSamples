using System;
using UnityEngine;

public class LowPassFilter
{
    private float alpha;
    private float lastValue;
    private float lastFilteredValue;
    private bool isFirstTime = true;

    public LowPassFilter(float alpha)
    {
        this.alpha = alpha;
        this.lastValue = 0.0f;
        this.lastFilteredValue = 0.0f;
    }

    public float Filter(float value, float timestamp, float alpha = -1)
    {
        float filteredValue;

        if (alpha != -1)
        {
            this.alpha = alpha;
        }

        if (isFirstTime)
        {
            filteredValue = value;
            this.isFirstTime = false;
        }
        else
        {
            filteredValue = this.alpha * value + (1.0f - this.alpha) * this.lastFilteredValue;
        }

        lastValue = value;
        this.lastFilteredValue = filteredValue;

        return this.lastFilteredValue;
    }

    public float GetLastFilteredValue()
    {
        return this.lastFilteredValue;
    }

    public float GetLastValue()
    {
        return this.lastValue;
    }

    public void reset()
    {
        this.isFirstTime = true;
    }
}
public class OneEuroFilter
{
    public float frequency; // <= 0
    public float mincutoff; // <= 0, default should be 1.0
    public float beta; // default should be 0.0
    public float dcutoff; // <= 0, default should be 1.0
    public LowPassFilter x;
    public LowPassFilter dx;
    public float lasttime;

    public OneEuroFilter(float frequency, float mincutoff, float beta, float dcutoff)
    {
        this.frequency = frequency;
        this.mincutoff = mincutoff;
        this.beta = beta;
        this.dcutoff = dcutoff;
        this.x = new LowPassFilter(Alpha(mincutoff));
        this.dx = new LowPassFilter(Alpha(dcutoff));
        this.lasttime = 0;
    }

    public float Alpha(float cutoff)
    {
        float te = 1.0f / this.frequency;
        float tau = 1.0f / (2.0f * (float)Math.PI * cutoff);

        return 1.0f / (1.0f + tau / te);
    }

    public float Filter(float value, float timestamp)
    {
        if (this.lasttime != 0 && timestamp > this.lasttime)
        {
            this.frequency = 1.0f / (timestamp - this.lasttime);
        }

        this.lasttime = timestamp;

        float prev_value = this.x.GetLastFilteredValue();

        float delta_x;
        if (prev_value == 0.0f)
        {
            delta_x = 0.0f;
        }
        else
        {
            delta_x = (value - prev_value) * this.frequency;
        }

        float edx = this.dx.Filter(delta_x, timestamp, Alpha(this.dcutoff));

        float cutoff = this.mincutoff + this.beta * Math.Abs(edx);

        return this.x.Filter(value, timestamp, Alpha(cutoff));
    }

    void reset()
    {
        this.x.reset();
        this.dx.reset();
        this.lasttime = 0;
    }
}

public class OneEuroVector3
{
    private OneEuroFilter x, y, z;

    public OneEuroVector3(float mincutoff, float beta)
    {
        x = new OneEuroFilter(60, mincutoff, beta, 1.0f);
        y = new OneEuroFilter(60, mincutoff, beta, 1.0f);
        z = new OneEuroFilter(60, mincutoff, beta, 1.0f);
    }

    public Vector3 Filter(Vector3 input, float timestamp)
    {
        return new Vector3(
            x.Filter(input.x, timestamp),
            y.Filter(input.y, timestamp),
            z.Filter(input.z, timestamp)
        );
    }

    public void UpdateParams(float minCutoff, float beta)
    {
        // Apply new values to all internal filters
        x.mincutoff = y.mincutoff = z.mincutoff = minCutoff;
        x.beta = y.beta = z.beta = beta;
    }
}

public class OneEuroQuaternion
{
    private OneEuroFilter x, y, z, w;
    private Quaternion lastRawRot = Quaternion.identity;

    public OneEuroQuaternion(float mincutoff, float beta)
    {
        x = new OneEuroFilter(60, mincutoff, beta, 1.0f);
        y = new OneEuroFilter(60, mincutoff, beta, 1.0f);
        z = new OneEuroFilter(60, mincutoff, beta, 1.0f);
        w = new OneEuroFilter(60, mincutoff, beta, 1.0f);
    }

    public Quaternion Filter(Quaternion input, float timestamp)
    {
        // 1. Hemisphere check: Ensure the new rotation is in the same 
        // 'half' of the hypersphere as the last one to prevent 'flipping'
        if (Quaternion.Dot(lastRawRot, input) < 0.0f)
        {
            input = new Quaternion(-input.x, -input.y, -input.z, -input.w);
        }
        lastRawRot = input;

        // 2. Filter components individually
        float fX = x.Filter(input.x, timestamp);
        float fY = y.Filter(input.y, timestamp);
        float fZ = z.Filter(input.z, timestamp);
        float fW = w.Filter(input.w, timestamp);

        // 3. Normalization is MANDATORY for Quaternions
        return new Quaternion(fX, fY, fZ, fW).normalized;
    }

    public void UpdateParams(float minCutoff, float beta)
    {
        // Apply new values to all internal filters
        x.mincutoff = y.mincutoff = z.mincutoff = w.mincutoff = minCutoff;
        x.beta = y.beta = z.beta = w.beta = beta;
    }
}