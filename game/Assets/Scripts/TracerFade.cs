using System.Collections;
using UnityEngine;

/// <summary>
/// Auto-added by WeaponController to any spawned tracer.
/// You do not need to attach this manually anywhere.
/// </summary>
public class TracerFade : MonoBehaviour
{
    private LineRenderer _line;
    private float _duration;

    public void Init(LineRenderer line, float duration)
    {
        _line = line;
        _duration = duration;
        StartCoroutine(FadeAndDestroy());
    }

    private IEnumerator FadeAndDestroy()
    {
        float t = 0f;
        Color startColor = _line.material.color;

        while (t < _duration)
        {
            t += Time.deltaTime;
            float alpha = Mathf.Lerp(1f, 0f, t / _duration);
            _line.material.color = new Color(startColor.r, startColor.g, startColor.b, alpha);
            yield return null;
        }

        Destroy(gameObject);
    }
}
