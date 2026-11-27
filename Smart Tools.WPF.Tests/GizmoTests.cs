using Smart_Tools.WPF.Controllers;
using Xunit;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>The axis gizmo follows the camera, and a click on an axis looks from that side.</summary>
    public class GizmoTests
    {
        [Fact]
        public void Seen_from_the_front_X_points_right_Y_up_and_Z_at_the_viewer()
        {
            var cam = new OrbitCamera { Yaw = 0, Pitch = 0 };
            var axes = cam.GizmoAxes();
            Assert.Equal(1, axes[0].X, 9);
            Assert.Equal(0, axes[0].Y, 9);
            Assert.Equal(0, axes[1].X, 9);
            Assert.Equal(-1, axes[1].Y, 9);   // up on screen = negative Y
            Assert.Equal(-1, axes[2].Depth, 9);   // Z comes towards the viewer
        }

        [Fact]
        public void Seen_from_the_right_Z_points_left_and_X_at_the_viewer()
        {
            var cam = new OrbitCamera();
            cam.LookFrom(0, positive: true);
            var axes = cam.GizmoAxes();
            Assert.Equal(-1, axes[2].X, 9);
            Assert.Equal(-1, axes[0].Depth, 9);
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(0, false)]
        [InlineData(2, true)]
        [InlineData(2, false)]
        public void Looking_from_a_side_puts_the_camera_on_that_side(int axis, bool positive)
        {
            var cam = new OrbitCamera { Yaw = 123, Pitch = 40, Distance = 1000 };
            cam.LookFrom(axis, positive);
            var p = cam.Position;
            double along = axis == 0 ? p.X : p.Z;
            Assert.Equal(positive ? 1000 : -1000, along, 6);
            Assert.Equal(0, p.Y, 6);
        }

        [Fact]
        public void Looking_from_above_or_below_goes_as_far_as_the_pitch_allows_and_keeps_the_turn()
        {
            var cam = new OrbitCamera { Yaw = 30, Pitch = 10 };
            cam.LookFrom(1, positive: true);
            Assert.Equal(OrbitCamera.MaxPitch, cam.Pitch);
            Assert.Equal(30, cam.Yaw);
            cam.LookFrom(1, positive: false);
            Assert.Equal(OrbitCamera.MinPitch, cam.Pitch);
        }

        [Fact]
        public void Every_axis_stays_a_unit_vector_as_the_camera_turns()
        {
            var cam = new OrbitCamera();
            for (int i = 0; i < 20; i++)
            {
                cam.Orbit(37, 9);
                foreach (var a in cam.GizmoAxes())
                    Assert.Equal(1, a.X * a.X + a.Y * a.Y + a.Depth * a.Depth, 9);
            }
        }
    }
}
