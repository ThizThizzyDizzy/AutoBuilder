using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace AutoBuilder
{
    public class AutoBuilderBuildMetadata : MonoBehaviour
    {
        public TextMeshPro textObject;

        public virtual void SetBuildMetadata(string metadata)
        {
            textObject.text = $"{metadata}";
        }
    }
}