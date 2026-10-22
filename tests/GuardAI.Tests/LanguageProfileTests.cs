using GuardAI.Checks.Assets;

namespace GuardAI.Tests;

/// <summary>
/// C 系多语言剖析参数测试：Java / Kotlin / TS / JS 的语法差异与"只漏不误"防线。
/// </summary>
public class LanguageProfileTests
{
    [Fact]
    public void Java_注解带等号与throws尾巴仍命中_字段初始化不冒充()
    {
        var source = """
            package demo;

            public class Repo {
                @Column(name = "x")
                public void Save(Entity e) throws IOException {
                    db.write(e);
                }

                void Helper(int a) {
                    run(a);
                }

                private Gson gson = new Gson();

                Runnable task = () -> {
                    doThing();
                };
            }
            """;

        var members = CLikeMemberParser.Parse(source, "src/Repo.java", LanguageProfile.Java);

        // Save（注解实参带 = 不拦）与 Helper（包级私有也收）；gson/匿名 lambda 行都不是声明
        Assert.Collection(members,
            m => Assert.Equal("Save", m.Name),
            m => Assert.Equal("Helper", m.Name));
    }

    [Fact]
    public void Java_lambda调用与匿名类不冒充声明()
    {
        var source = """
            public class Holder {
                Handler h = makeHandler(x -> {
                    process(x);
                });

                void Init() {
                    list.forEach(x -> handle(x));
                }
            }
            """;

        var members = CLikeMemberParser.Parse(source, "src/Holder.java", LanguageProfile.Java);

        // Init 的方法体整段消费，体内的 forEach/handle 调用不外漏；字段初始化行被声明形态闸拦下
        var member = Assert.Single(members);
        Assert.Equal("Init", member.Name);
    }

    [Fact]
    public void Kotlin_返回类型尾巴命中_属性初始化与单表达式函数不冒充()
    {
        var source = """
            class Repo {
                fun Save(e: Entity): Int {
                    return db.write(e)
                }

                val gson = Gson()

                fun Tiny() = 42
                fun Next() {
                    run()
                }
            }
            """;

        var members = CLikeMemberParser.Parse(source, "src/Repo.kt", LanguageProfile.Kotlin);

        // Save（返回类型尾巴）与 Next 命中；Tiny 单表达式函数按只漏不误放弃（无终结符尾段），
        // val gson = Gson() 属性初始化不冒充——绝不产生幻影成员
        Assert.Collection(members,
            m => Assert.Equal("Save", m.Name),
            m => Assert.Equal("Next", m.Name));
    }

    [Fact]
    public void TS_装饰器与箭头属性不冒充_类方法命中()
    {
        var source = """
            @Component({
                selector: 'app'
            })
            export class Foo {
                handler = (e) => {
                    handle(e);
                };

                Run(a: number): void {
                    this.handle(a);
                }
            }
            """;

        var members = CLikeMemberParser.Parse(source, "src/foo.ts", LanguageProfile.TypeScript);

        // 装饰器被剥空后不构成声明；handler 是箭头属性；只有 Run 是方法
        var member = Assert.Single(members);
        Assert.Equal("Run", member.Name);
    }

    [Fact]
    public void JS_class方法与顶层function收_测试框架调用不收()
    {
        var source = """
            class Api {
                Fetch(url) {
                    return get(url);
                }
            }

            function BuildAll(items) {
                return items.map(x => x);
            }

            describe('suite', () => {
                it('case', () => {
                    run();
                });
            });
            """;

        var members = CLikeMemberParser.Parse(source, "src/api.js", LanguageProfile.JavaScript);

        // describe/it 是测试框架关键字（黑名单）；顶层 function 归无类型成员
        Assert.Collection(members,
            m =>
            {
                Assert.Equal("Fetch", m.Name);
                Assert.Equal("Api", m.TypeName);
            },
            m =>
            {
                Assert.Equal("BuildAll", m.Name);
                Assert.Equal(string.Empty, m.TypeName);
            });
    }

    [Fact]
    public void Java_doc注释进摘要()
    {
        var source = """
            public class Repo {
                // 保存实体到存储
                public void Save(Entity e) {
                    db.write(e);
                }
            }
            """;

        var members = CLikeMemberParser.Parse(source, "src/Repo.java", LanguageProfile.Java);

        var member = Assert.Single(members);
        Assert.Equal("保存实体到存储", member.Summary);
    }
}
