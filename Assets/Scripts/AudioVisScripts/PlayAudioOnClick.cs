using UnityEngine;

public class PlayAudioOnClick : MonoBehaviour
{
    [SerializeField] private FMODAudioVisualizer audioVisualizer;
    [SerializeField] private bool toggleOnClick = true;

    private bool isPlaying;

    private void Awake()
    {
        if (audioVisualizer == null)
            audioVisualizer = GetComponent<FMODAudioVisualizer>();
    }

    private void OnMouseDown()
    {
        if (audioVisualizer == null)
        {
            Debug.LogError("No FMODAudioVisualizer is assigned.", this);
            return;
        }

        if (toggleOnClick && isPlaying)
        {
            audioVisualizer.StopFMODEvent();
            isPlaying = false;
        }
        else
        {
            audioVisualizer.PlayFMODEvent();
            isPlaying = true;
        }
    }
}