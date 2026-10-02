using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class FollowPlayerHUD : MonoBehaviour
{
    private Camera cam;

    public GameObject player;

    private void Awake()
    {
        cam = Camera.main;
    }

    private void LateUpdate()
    {
        Vector3 vec = cam.WorldToScreenPoint(player.gameObject.transform.position);
        transform.position = vec;
    }
}
