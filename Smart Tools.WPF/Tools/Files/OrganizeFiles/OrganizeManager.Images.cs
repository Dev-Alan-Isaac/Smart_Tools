using ImageMagick;
using Orientation = ImageMagick.Orientation;
using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Image header reads for the image rules (Magick.NET ping - no pixels decoded).</summary>
    public sealed partial class OrganizeManager
    {
        public static OrganizeImageFacts? ReadImageFacts(string path)
        {
            using var image = new MagickImage();
            image.Ping(path);

            // Phone photos taken in portrait store landscape pixels plus a
            // "rotate" tag - report the size as it's shown.
            bool rotated = image.Orientation is Orientation.LeftTop or Orientation.RightTop
                or Orientation.RightBottom or Orientation.LeftBottom;
            int w = (int)image.Width, h = (int)image.Height;
            if (rotated) (w, h) = (h, w);

            return new OrganizeImageFacts(w, h, image.Format.ToString(), (int)image.Depth);
        }
    }
}
