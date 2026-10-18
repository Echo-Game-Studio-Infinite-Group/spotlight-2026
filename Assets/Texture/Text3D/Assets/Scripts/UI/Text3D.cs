using TMPro;
using UnityEngine;

public class Text3D : MonoBehaviour
{
    private void Start()
    {
        GetComponent<Animator>().SetBool("Is3D", true);
    }
}
