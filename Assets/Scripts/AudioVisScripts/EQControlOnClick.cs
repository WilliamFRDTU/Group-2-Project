using UnityEngine;
using UnityEngine.InputSystem;
using FMOD;
using FMODUnity;

public class EQControlOnClick : MonoBehaviour
{
    [SerializeField] private string parameterName = "BassBoost";
    [SerializeField] private Transform mappedObject;
    [SerializeField] private float minimumZ = -1f;
    [SerializeField] private float maximumZ = 1f;
    [SerializeField] private float minimumParameterValue = -10f;
    [SerializeField] private float maximumParameterValue = 10f;
    [SerializeField] private float zUnitsPerPixel = 0.01f;

    private bool isDragging;
    private float dragStartMouseY;
    private float dragStartZ;

    private void Start()
    {
        if (mappedObject == null)
            mappedObject = transform;

        if (Mathf.Approximately(minimumZ, maximumZ))
        {
            UnityEngine.Debug.LogError("Minimum Z and Maximum Z must be different.", this);
            enabled = false;
            return;
        }

        SetEQFromPosition();
    }

    private void Update()
    {
        if (isDragging)
        {
            if (Mouse.current == null)
                return;

            float mouseDeltaY = Mouse.current.position.ReadValue().y - dragStartMouseY;
            float newZ = dragStartZ + mouseDeltaY * zUnitsPerPixel;
            Vector3 position = mappedObject.position;
            position.z = Mathf.Clamp(newZ, minimumZ, maximumZ);
            mappedObject.position = position;
        }

        SetEQFromPosition();
    }

    private void OnMouseDown()
    {
        if (mappedObject == null)
            return;

        if (Mouse.current == null)
        {
            UnityEngine.Debug.LogError("No mouse device is available.", this);
            return;
        }

        isDragging = true;
        dragStartMouseY = Mouse.current.position.ReadValue().y;
        dragStartZ = mappedObject.position.z;
    }

    private void OnMouseUp()
    {
        isDragging = false;
    }

    private void SetEQFromPosition()
    {
        float normalizedZ = Mathf.InverseLerp(
            minimumZ,
            maximumZ,
            mappedObject.position.z);
        float parameterValue = Mathf.Lerp(
            minimumParameterValue,
            maximumParameterValue,
            normalizedZ);

        RESULT result = RuntimeManager.StudioSystem.setParameterByName(
            parameterName,
            parameterValue);

        if (result != RESULT.OK)
        {
            UnityEngine.Debug.LogError(
                "Failed to set FMOD parameter '" + parameterName + "': " + result,
                this);
        }
    }
}