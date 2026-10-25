using System.Collections;
using UnityEngine;

/// <summary>
/// Attach to the muzzle point (empty GameObject at the gun barrel tip).
/// Child it under the gun model. Add a ParticleSystem (your muzzle flash VFX Graph
/// or Shuriken effect) and a Light component as children/siblings and assign below.
/// </summary>
public class MuzzleFlashController : MonoBehaviour
{
    [SerializeField] private ParticleSystem muzzleParticles;
    [SerializeField] private Light muzzleLight;
    [SerializeField] private float lightDuration = 0.05f;
    [SerializeField] private float lightIntensity = 8f;

    public void Play()
    {
        if (muzzleParticles != null)
        {
            muzzleParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            muzzleParticles.Play();
        }

        if (muzzleLight != null)
        {
            StopAllCoroutines();
            StartCoroutine(FlashLight());
        }
    }

    private IEnumerator FlashLight()
    {
        muzzleLight.enabled = true;
        muzzleLight.intensity = lightIntensity;
        yield return new WaitForSeconds(lightDuration);
        muzzleLight.enabled = false;
    }
}
