using UnityEngine;
using UnityEngine.UIElements;

namespace Lvn.UI
{
    /// <summary>
    /// The full-screen background layer (z-order 0). Shows a sprite cropped to
    /// fill, or a solid colour when there's no art. A plain
    /// <see cref="VisualElement"/> — the stage feeds it sprites resolved through
    /// <see cref="ILvnAssets"/>.
    /// </summary>
    public sealed class BackgroundLayer : VisualElement
    {
        public BackgroundLayer()
        {
            style.position = Position.Absolute;
            style.left = 0;
            style.right = 0;
            style.top = 0;
            style.bottom = 0;
            style.backgroundColor = Color.black;
            pickingMode = PickingMode.Ignore;
        }

        public void SetSprite(Sprite sprite)
        {
            if (sprite == null) return;
            style.backgroundImage = new StyleBackground(sprite);
            // Cover the layer, cropping overflow (the modern replacement for the
            // deprecated unityBackgroundScaleMode = ScaleAndCrop).
            style.backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
            style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
        }

        public void SetColor(Color color)
        {
            style.backgroundImage = StyleKeyword.None;
            style.backgroundColor = color;
        }
    }
}
