using Juvo.Domain.Content;

namespace Juvo.Domain.Tests.Content;

public class ContactMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static ContactMessage Make(string? mobile = "+8801712345678", string? email = null) =>
        new("  Nadia  ", mobile, email, " ", "  Where is my parcel?  ", "/contact", "abc", Now);

    [Fact]
    public void A_new_message_is_new_trimmed_and_has_a_reference()
    {
        var m = Make();

        Assert.Equal(ContactMessageStatus.New, m.Status);
        Assert.Equal("Nadia", m.Name);
        Assert.Equal("Where is my parcel?", m.Message);
        Assert.Null(m.Subject);
        Assert.Equal(Now, m.ReceivedAt);
        Assert.Matches("^CM-[0-9A-F]{8}$", m.Reference);
        Assert.Null(m.HandledBy);
    }

    [Fact]
    public void A_mobile_or_an_email_is_required()
    {
        Assert.Throws<ArgumentException>(() => Make(null, null));
        Assert.Throws<ArgumentException>(() => Make("  ", " "));
        Assert.Equal("a@b.com", Make(null, "a@b.com").Email);
    }

    [Fact]
    public void Setting_a_status_stamps_who_and_when_and_new_clears_it()
    {
        var m = Make();
        var staff = Guid.NewGuid();

        Assert.True(m.SetStatus(ContactMessageStatus.Read, staff, Now.AddMinutes(5)));
        Assert.Equal(staff, m.HandledBy);
        Assert.Equal(Now.AddMinutes(5), m.HandledAt);
        Assert.False(m.SetStatus(ContactMessageStatus.Read, staff, Now.AddMinutes(9)));
        Assert.Equal(Now.AddMinutes(5), m.HandledAt);

        Assert.True(m.SetStatus(ContactMessageStatus.Archived, staff, Now.AddMinutes(10)));
        Assert.True(m.SetStatus(ContactMessageStatus.New, staff, Now.AddMinutes(11)));
        Assert.Null(m.HandledBy);
        Assert.Null(m.HandledAt);
    }

    [Fact]
    public void Notes_are_appended_with_author_and_time_and_capped()
    {
        var m = Make();

        Assert.True(m.AddNote("Called, no answer", "Rina", Now));
        Assert.True(m.AddNote("Second try", null, Now.AddHours(1)));
        Assert.Equal("[2026-11-22 10:00 UTC, Rina] Called, no answer\n[2026-11-22 11:00 UTC, staff] Second try", m.InternalNote);

        Assert.False(m.AddNote(new string('x', ContactMessage.InternalNoteMax), "Rina", Now));
        Assert.Contains("Second try", m.InternalNote);
    }
}
