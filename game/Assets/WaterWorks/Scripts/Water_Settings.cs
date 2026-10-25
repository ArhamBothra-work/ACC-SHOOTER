using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class Water_Settings : MonoBehaviour
{
    Material waterVolume;
    Material waterMaterial;

    void Update()
    {
        if (waterVolume == null)
        {
            waterVolume = (Material)Resources.Load("Water_Volume");
        }

        if (waterMaterial == null)
        {
            waterMaterial = GetComponent<MeshRenderer>().sharedMaterial;
        }

        if (waterVolume == null || waterMaterial == null)
            return;

        // Treating the water as a flat sheet for now: bounds height and
        // displacement both default to 0 until those features are added
        // to the shaders. Using HasProperty avoids errors either way.
        float boundsHeight = waterVolume.HasProperty("bounds")
            ? waterVolume.GetVector("bounds").y
            : 0f;

        float displacement = waterMaterial.HasProperty("_Displacement_Amount")
            ? waterMaterial.GetFloat("_Displacement_Amount")
            : 0f;

        waterVolume.SetVector(
            "pos",
            new Vector4(0, (boundsHeight / -2f) + transform.position.y + (displacement / 3f), 0, 0)
        );
    }
}