using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Attach to a UI Canvas element (e.g. a floating health bar above a target,
/// or the player's own HUD health bar).
/// Hierarchy required:
///   HealthBarBackground (Image, dark grey)
///    └─ RedBar   (Image, Type=Filled, Horizontal, color=red)
///        └─ GreenBar (Image, Type=Filled, Horizontal, color=green)
/// Assign redBar and greenBar below. Assign targetHealth to the Health
/// component this bar is tracking.
/// </summary>
public class HealthBarUI : MonoBehaviour
{
    [SerializeField] private Image greenBar;
    [SerializeField] private Image redBar;
    [SerializeField] private Health targetHealth;

    [Header("Timing")]
    [SerializeField] private float drainDelay = 0.3f;
    [SerializeField] private float drainDuration = 0.4f;

    private Coroutine _drainRoutine;

    private void OnEnable()
    {
        if (targetHealth != null)
        {
            targetHealth.CurrentHealth.OnValueChanged += OnHealthChanged;
            // Initialize bar to current value on enable.
            float percent = targetHealth.CurrentHealth.Value / targetHealth.MaxHealth;
            greenBar.fillAmount = percent;
            redBar.fillAmount = percent;
        }
    }

    private void OnDisable()
    {
        if (targetHealth != null)
        {
            targetHealth.CurrentHealth.OnValueChanged -= OnHealthChanged;
        }
    }

    private void OnHealthChanged(float oldValue, float newValue)
    {
        float newPercent = Mathf.Clamp01(newValue / targetHealth.MaxHealth);

        // Green bar snaps down immediately.
        greenBar.fillAmount = newPercent;

        // Red bar holds at the old value, then drains down after a short delay.
        if (_drainRoutine != null)
            StopCoroutine(_drainRoutine);

        _drainRoutine = StartCoroutine(DrainRedBar(redBar.fillAmount, newPercent));
    }

    private IEnumerator DrainRedBar(float from, float to)
    {
        yield return new WaitForSeconds(drainDelay);

        float t = 0f;
        while (t < drainDuration)
        {
            t += Time.deltaTime;
            redBar.fillAmount = Mathf.Lerp(from, to, t / drainDuration);
            yield return null;
        }

        redBar.fillAmount = to;
    }
}
