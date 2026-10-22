using GuardAI.Checks.Assets;

namespace GuardAI.Tests;

/// <summary>
/// Go 适配器测试：导出可见性、接收者归属、多行参数、注释摘要。
/// </summary>
public class GoMemberParserTests
{
    [Fact]
    public void 导出方法与接收者类型_未导出跳过()
    {
        var source = """
            package repo

            // Save 保存实体
            func (r *Repo) Save(e Entity) (int, error) {
                return r.db.Write(e)
            }

            func (r *Repo) helper() {
                log.Println("x")
            }

            func BuildAll(items []Item) []Item {
                return items
            }
            """;

        var members = GoMemberParser.Parse(source, "internal/repo.go");

        Assert.Collection(members,
            m =>
            {
                Assert.Equal("Save", m.Name);
                Assert.Equal("Repo", m.TypeName);
                Assert.Equal("Save 保存实体", m.Summary);
            },
            m =>
            {
                Assert.Equal("BuildAll", m.Name);
                Assert.Equal(string.Empty, m.TypeName);
            });
    }

    [Fact]
    public void 多行参数与函数体()
    {
        var source = """
            func Render(
                w io.Writer,
                data any,
            ) error {
                return tmpl.Execute(w, data)
            }
            """;

        var members = GoMemberParser.Parse(source, "render.go");

        var member = Assert.Single(members);
        Assert.Equal("Render", member.Name);
        Assert.Contains("tmpl.Execute", member.BodyText);
    }

    [Fact]
    public void 无函数体声明跳过_反引号字符串不干扰()
    {
        var source = """
            func External(x int) int

            func Query(sql string) string {
                raw := `select *
                    from t`
                return raw + sql
            }
            """;

        var members = GoMemberParser.Parse(source, "db.go");

        // External 是汇编桩（无函数体）按只漏不误跳过；反引号多行串不炸花括号/括号配平
        var member = Assert.Single(members);
        Assert.Equal("Query", member.Name);
    }
}
