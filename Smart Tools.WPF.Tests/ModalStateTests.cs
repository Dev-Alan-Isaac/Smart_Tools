using Smart_Tools.WPF.Views;

namespace Smart_Tools.WPF.Tests
{
    /// <summary>
    /// "Is a modal open?" - the grid outlines (drawn in the adorner layer, above all content) hide while it is,
    /// otherwise they cross the dimmed page and the modal card.
    /// </summary>
    [Collection("ModalState")]   // one static counter: these tests must not run side by side
    public class ModalStateTests
    {
        public ModalStateTests() => ModalState.Reset();

        [Fact]
        public void Nothing_is_open_to_begin_with() => Assert.False(ModalState.AnyOpen);

        [Fact]
        public void A_modal_is_open_between_Opened_and_Closed()
        {
            ModalState.Opened();
            Assert.True(ModalState.AnyOpen);
            ModalState.Closed();
            Assert.False(ModalState.AnyOpen);
        }

        [Fact]
        public void Stacked_modals_keep_it_open_until_the_last_one_closes()
        {
            ModalState.Opened();
            ModalState.Opened();
            ModalState.Closed();
            Assert.True(ModalState.AnyOpen);
            ModalState.Closed();
            Assert.False(ModalState.AnyOpen);
        }

        [Fact]
        public void An_extra_Closed_never_leaves_it_stuck_negative()
        {
            ModalState.Closed();
            ModalState.Opened();
            Assert.True(ModalState.AnyOpen);
            ModalState.Closed();
            Assert.False(ModalState.AnyOpen);
        }

        [Fact]
        public void Listeners_hear_every_change()
        {
            int heard = 0;
            void OnChanged() => heard++;
            ModalState.Changed += OnChanged;
            try
            {
                ModalState.Opened();
                ModalState.Closed();
            }
            finally
            {
                ModalState.Changed -= OnChanged;
            }
            Assert.Equal(2, heard);
        }
    }
}
