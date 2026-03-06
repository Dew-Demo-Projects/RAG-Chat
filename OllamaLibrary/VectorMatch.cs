using System;

namespace OllamaClient
{
    public static class VectorMath
    {
        public static float Dot(float[] a, float[] b)
        {
            if (a == null || b == null) throw new ArgumentNullException();
            if (a.Length != b.Length) throw new ArgumentException("Wektory muszą mieć tę samą długość.");

            float sum = 0f;
            for (int i = 0; i < a.Length; i++)
                sum += a[i] * b[i];

            return sum;
        }

        public static float Length(float[] v)
        {
            if (v == null) throw new ArgumentNullException();

            float sum = 0f;
            for (int i = 0; i < v.Length; i++)
                sum += v[i] * v[i];

            return MathF.Sqrt(sum);
        }
    }
}