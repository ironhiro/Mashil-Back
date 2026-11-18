using Mashil.Api.Endpoints;
using Mashil.Domain;

namespace Mashil.Api.Tests;

/// <summary>작성자 이름과 구분 번호 (닉네임 중복 허용, 이름#1234). 웹 src/lib/userTag.ts 와 같은 값이어야 한다.</summary>
public class CommentAuthorTests
{
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "0000")]
    [InlineData("0001e240-0000-0000-0000-000000000000", "3456")] // 0x0001e240 = 123456
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", "7295")] // 4294967295 % 10000
    [InlineData("3F2A9C1E-5B7D-4E8A-9C21-7D4E5F6A8B90", "7086")] // 0x3f2a9c1e = 1059757086
    public void Tag_is_first_8_hex_digits_mod_10000(string id, string tag) =>
        Assert.Equal(tag, CommentAuthor.Tag(Guid.Parse(id)));

    [Fact]
    public void Name_has_tag_except_for_deleted_users()
    {
        var id = Guid.Parse("0001e240-0000-0000-0000-000000000000");
        Assert.Equal("길동#3456", CommentAuthor.Name("길동", UserStatus.Active, id));
        Assert.Equal("익명#3456", CommentAuthor.Name(null, UserStatus.Active, id));
        Assert.Equal(UserProfile.DeletedDisplayName, CommentAuthor.Name("길동", UserStatus.Deleted, id));
    }
}
