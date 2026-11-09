using UnityEngine;

namespace DeadEverywhere.FloridaGeneration
{
    public class WorldChunk : MonoBehaviour
    {
        /// <summary>The residential reference is 10 × 10 metres at transform scale one.</summary>
        public const float RoadTileSize = 10f;

        [Tooltip("10 × 10 metre residential reference at scale 1. Generated roads use the same metre scale, " +
                 "with procedural asphalt and separate lane markings rather than stretching this tile's paint.")]
        [SerializeField] private GameObject roadTile_2Lane;

        public GameObject ResidentialRoadReference => roadTile_2Lane;
    }
}
