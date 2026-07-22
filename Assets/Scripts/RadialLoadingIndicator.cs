using UnityEngine;
using UnityEngine.UI;

// Animates a Filled/Radial Image so its moving edge always sweeps in one consistent
// direction, alternating every cycleSeconds between filling the pie (growing forward
// from the fixed origin) and clearing it (the gap opens at that same origin and grows
// forward the same way). Simply ping-ponging fillAmount between 0 and 1 makes the
// visible edge retrace itself backward every time it turns around - flipping
// fillClockwise each lap instead keeps the edge's on-screen motion moving the same way
// throughout, so a lap boundary reads as "switching mode" rather than "undoing".
[RequireComponent(typeof(Image))]
public class RadialLoadingIndicator : MonoBehaviour
{
    [SerializeField] private float cycleSeconds = 3.5f;

    private Image image;

    void Awake()
    {
        image = GetComponent<Image>();
    }

    void Update()
    {
        float laps = Time.time / cycleSeconds;
        int lapIndex = Mathf.FloorToInt(laps);
        float lapFraction = laps - lapIndex;
        bool filling = lapIndex % 2 == 0;

        image.fillClockwise = filling;
        image.fillAmount = filling ? lapFraction : 1f - lapFraction;
    }
}
