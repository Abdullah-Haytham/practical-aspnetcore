using FluentValidation;
using FluentValidation.AspNetCore;
using Ganss.Xss;
using HtmlBuilders;
using LiteDB;
using Markdig;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Scriban;
using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using static HtmlBuilders.HtmlTags;

const string DisplayDateFormat = "MMMM dd, yyyy";
const string HomePageName = "home-page";
const string LogInPageName = "login-page";
const string RegisterPageName = "register-page";
const string HtmlMime = "text/html";

var builder = WebApplication.CreateBuilder();
builder.Services
  .AddSingleton<Wiki>()
  .AddSingleton<Render>()
  .AddAntiforgery()
  .AddAuthorization()
  .AddMemoryCache()
  .AddAuthentication(options =>
  {
      options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
      options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
  }).AddCookie();

builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();

app.UseStaticFiles();
app.UseAuthorization();

// Load home page
app.MapGet("/", (HttpContext context, Wiki wiki, Render render) =>
{
    var isLogged = isLoggedIn(context);
    Page? page = wiki.GetPage(HomePageName);

    if (page is not object)
        return Results.Redirect($"/{HomePageName}");

    return Results.Text(render.BuildPage(HomePageName, atBody: () =>
        new[]
        {
          RenderPageContent(page),
          RenderPageAttachments(page),
          isLogged ? A.Href($"/edit?pageName={HomePageName}").Class("uk-button uk-button-default uk-button-small").Append("Edit").ToHtmlString() : ""
        },
        atSidePanel: () => AllPages(wiki),
        isLoggedIn: isLogged
      ).ToString(), HtmlMime);
});

// Load login page
app.MapGet("/auth/login", (Render render, HttpContext context, IAntiforgery antiForgery) =>
{
    var stop = "";
    return Results.Text(render.BuildPage(LogInPageName, atBody: () => new[]
    {
        BuildAuthForm(true, antiForgery.GetAndStoreTokens(context))
    }
    ).ToString(), HtmlMime);   
});

app.MapGet("/auth/register", (Render render, HttpContext context, IAntiforgery antiForgery) =>
{
    var stop = "";
    return Results.Text(render.BuildPage(RegisterPageName, atBody: () => new[]
    {
        BuildAuthForm(false, antiForgery.GetAndStoreTokens(context))
    }
    ).ToString(), HtmlMime);
});

app.MapGet("/new-page", (string? pageName, HttpContext context) =>
{
    var isLogged = isLoggedIn(context);
    if (!isLogged)
    {
        return Results.BadRequest("Access Denied. Please Login to use this feature");
    }

    if (string.IsNullOrEmpty(pageName))
        return Results.Redirect("/");

    // Copied from https://www.30secondsofcode.org/c-sharp/s/to-kebab-case
    string ToKebabCase(string str)
    {
        Regex pattern = new Regex(@"[A-Z]{2,}(?=[A-Z][a-z]+[0-9]*|\b)|[A-Z]?[a-z]+[0-9]*|[A-Z]|[0-9]+");
        return string.Join("-", pattern.Matches(str)).ToLower();
    }

    var page = ToKebabCase(pageName!);
    return Results.Redirect($"/{page}");
});

// Edit a wiki page
app.MapGet("/edit", (string pageName, HttpContext context, Wiki wiki, Render render, IAntiforgery antiForgery) =>
{
    var isLogged = isLoggedIn(context);
    if (!isLogged)
    {
        return Results.BadRequest("Access Denied. Please Login to use this feature");
    }

    Page? page = wiki.GetPage(pageName);
    if (page is not object)
        return Results.NotFound();

    return Results.Text(render.BuildEditorPage(pageName,
      atBody: () =>
        new[]
        {
          BuildForm(new PageInput(page!.Id, pageName, page.Content, null), path: $"{pageName}", antiForgery: antiForgery.GetAndStoreTokens(context)),
          RenderPageAttachmentsForEdit(page!, antiForgery.GetAndStoreTokens(context))
        },
      atSidePanel: () =>
      {
          var list = new List<string>();
          // Do not show delete button on home page
          if (!pageName!.ToString().Equals(HomePageName, StringComparison.Ordinal))
              list.Add(RenderDeletePageButton(page!, antiForgery: antiForgery.GetAndStoreTokens(context)));

          list.Add(Br.ToHtmlString());
          list.AddRange(AllPagesForEditing(wiki));
          return list;
      }).ToString(), HtmlMime);
});

// Deal with attachment download
app.MapGet("/attachment", (string fileId, Wiki wiki) =>
{
    var file = wiki.GetFile(fileId);
    if (file is not object)
      return Results.NotFound();

    app!.Logger.LogInformation("Attachment " + file.Value.meta.Id + " - " + file.Value.meta.Filename);

    return Results.File(file.Value.file, file.Value.meta.MimeType);
});

// Load a wiki page
app.MapGet("/{pageName}", (string pageName, HttpContext context, Wiki wiki, Render render, IAntiforgery antiForgery) =>
{
    var isLogged = isLoggedIn(context);
    pageName = pageName ?? "";

    Page? page = wiki.GetPage(pageName);

    if (page is object)
    {
        return Results.Text(render.BuildPage(pageName, atBody: () =>
          new[]
          {
            RenderPageContent(page),
            RenderPageAttachments(page),
            Div.Class("last-modified").Append("Last modified: " + page!.LastModifiedUtc.ToString(DisplayDateFormat)).ToHtmlString(),
            isLogged ? A.Href($"/edit?pageName={pageName}").Append("Edit").ToHtmlString() : ""
            //RenderPageComments(page)?
          },
          atSidePanel: () => AllPages(wiki),
          isLoggedIn: isLogged
        ).ToString(), HtmlMime);
    }
    else
    {
        if (isLogged)
        {
            return Results.Text(render.BuildEditorPage(pageName,
            atBody: () =>
              new[]
              {
                BuildForm(new PageInput(null, pageName, string.Empty, null), path: pageName, antiForgery: antiForgery.GetAndStoreTokens(context))
              },
            atSidePanel: () => AllPagesForEditing(wiki)).ToString(), HtmlMime);
        }
        else
        {
            return Results.Redirect("/auth/login");
        }
        
    }
});

// Delete a page
app.MapPost("/delete-page", async (HttpContext context, IAntiforgery antiForgery, Wiki wiki) =>
{
    await antiForgery.ValidateRequestAsync(context);
    var id = context.Request.Form["Id"];

    if (StringValues.IsNullOrEmpty(id))
    {
        app.Logger.LogWarning($"Unable to delete page because form Id is missing");
        return Results.Redirect("/");
    }

    var (isOk, exception) = wiki.DeletePage(Convert.ToInt32(id), HomePageName);

    if (!isOk && exception is object)
        app.Logger.LogError(exception, $"Error in deleting page id {id}");
    else if (!isOk)
        app.Logger.LogError($"Unable to delete page id {id}");

    return Results.Redirect("/");
});

app.MapPost("/delete-attachment", async (HttpContext context, IAntiforgery antiForgery, Wiki wiki)=>
{
    await antiForgery.ValidateRequestAsync(context);
    var id = context.Request.Form["Id"];

    if (StringValues.IsNullOrEmpty(id))
    {
        app.Logger.LogWarning($"Unable to delete attachment because form Id is missing");
        return Results.Redirect("/");
    }

    var pageId = context.Request.Form["PageId"];
    if (StringValues.IsNullOrEmpty(pageId))
    {
        app.Logger.LogWarning($"Unable to delete attachment because form PageId is missing");
        return Results.Redirect("/");
    }

    var (isOk, page, exception) = wiki.DeleteAttachment(Convert.ToInt32(pageId), id.ToString());

    if (!isOk)
    {
        if (exception is object)
            app.Logger.LogError(exception, $"Error in deleting page attachment id {id}");
        else
            app.Logger.LogError($"Unable to delete page attachment id {id}");

        if (page is object)
            return Results.Redirect($"/{page.Name}");
        else
            return Results.Redirect("/");
    }

    return Results.Redirect($"/{page!.Name}");
});

// Add or update a wiki page
app.MapPost("/{pageName}", async (HttpContext context, Wiki wiki, Render render, IAntiforgery antiForgery)  =>
{
    var pageName = context.Request.RouteValues["pageName"] as string ?? "";
    await antiForgery.ValidateRequestAsync(context);

    PageInput input = PageInput.From(context.Request.Form);

    var modelState = new ModelStateDictionary();
    var validator = new PageInputValidator(pageName, HomePageName);
    validator.Validate(input).AddToModelState(modelState, null);

    if (!modelState.IsValid)
    {
        return Results.Text(render.BuildEditorPage(pageName,
          atBody: () =>
            new[]
            {
              BuildForm(input, path: $"{pageName}", antiForgery: antiForgery.GetAndStoreTokens(context), modelState)
            },
          atSidePanel: () => AllPages(wiki)).ToString(), HtmlMime);
    }

    var (isOk, p, ex) = wiki.SavePage(input);
    if (!isOk)
    {
        app.Logger.LogError(ex, "Problem in saving page");
        return Results.Problem("Problem in saving page");
    }

    return Results.Redirect($"/{p!.Name}");
});

app.MapPost("/auth/login", async (Wiki wiki, HttpContext context, Render render, IAntiforgery antiForgery) =>
{
    await antiForgery.ValidateRequestAsync(context);

    LoginInput input = LoginInput.From(context.Request.Form);

    var modelState = new ModelStateDictionary();
    var validator = new LoginInputValidator();
    validator.Validate(input).AddToModelState(modelState, null);

    if (!modelState.IsValid) 
    {
        return Results.Text(render.BuildPage(LogInPageName, atBody: () => new[]
        {
            BuildAuthForm(true, antiForgery.GetAndStoreTokens(context), modelState, login: input)
        }
        ).ToString(), HtmlMime);
    }

    var (isOk, user, ex) = wiki.CanLogin(input);
    if (!isOk)
    {
        return Results.Text(render.BuildPage(LogInPageName, atBody: () => new[]
        {
            BuildAuthForm(true, antiForgery.GetAndStoreTokens(context), modelState, login: input, err: ex.Message)
        }
        ).ToString(), HtmlMime);
    }
    else
    {
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, user!.Name)
        };
        var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var authProperties = new AuthenticationProperties
        {
            IsPersistent = true
        };
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(claimsIdentity), authProperties);
        return Results.Redirect("/");
    }
});

app.MapPost("/auth/register", async (Wiki wiki, HttpContext context, Render render, IAntiforgery antiForgery) =>
{
    await antiForgery.ValidateRequestAsync(context);

    RegisterInput input = RegisterInput.From(context.Request.Form);

    var modelState = new ModelStateDictionary();
    var validator = new RegisterInputValidator();
    validator.Validate(input).AddToModelState(modelState, null);

    if (!modelState.IsValid)
    {
        return Results.Text(render.BuildPage(RegisterPageName, atBody: () => new[]
        {
            BuildAuthForm(false, antiForgery.GetAndStoreTokens(context), modelState, register: input)
        }
        ).ToString(), HtmlMime);
    }

    var (isOk, ex) = wiki.RegisterUser(input);
    if (!isOk)
    {
        return Results.Text(render.BuildPage(RegisterPageName, atBody: () => new[]
        {
            BuildAuthForm(false, antiForgery.GetAndStoreTokens(context), modelState, register: input, err: ex.Message)
        }
        ).ToString(), HtmlMime);
    }
    else
    {
        return Results.Redirect("/auth/login");
    }
});

app.MapPost("/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    var referrer = context.Request.Headers["Referer"].ToString();
    if (!string.IsNullOrEmpty(referrer))
    {
        return Results.Redirect(referrer);
    }
    return Results.Redirect("/");
});

await app.RunAsync();

// End of the web part

static string[] AllPages(Wiki wiki) => new[]
{
  @"<span class=""uk-label"">Pages</span>",
  @"<ul class=""uk-list"">",
  string.Join("",
    wiki.ListAllPages().OrderBy(x => x.Name)
      .Select(x => Li.Append(A.Href(x.Name).Append(x.Name)).ToHtmlString()
    )
  ),
  "</ul>"
};

static string[] AllPagesForEditing(Wiki wiki)
{
    static string KebabToNormalCase(string txt) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(txt.Replace('-', ' '));

    return new[]
    {
      @"<span class=""uk-label"">Pages</span>",
      @"<ul class=""uk-list"">",
      string.Join("",
        wiki.ListAllPages().OrderBy(x => x.Name)
          .Select(x => Li.Append(Div.Class("uk-inline")
              .Append(Span.Class("uk-form-icon").Attribute("uk-icon", "icon: copy"))
              .Append(Input.Text.Value($"[{KebabToNormalCase(x.Name)}](/{x.Name})").Class("uk-input uk-form-small").Style("cursor", "pointer").Attribute("onclick", "copyMarkdownLink(this);"))
          ).ToHtmlString()
        )
      ),
      "</ul>"
    };
}

static string RenderMarkdown(string str)
{
    var sanitizer = new HtmlSanitizer();
    return sanitizer.Sanitize(Markdown.ToHtml(str, new MarkdownPipelineBuilder().UseSoftlineBreakAsHardlineBreak().UseAdvancedExtensions().Build()));
}

static string RenderPageContent(Page page) => RenderMarkdown(page.Content);

static string RenderDeletePageButton(Page page, AntiforgeryTokenSet antiForgery)
{
    var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);
    HtmlTag id = Input.Hidden.Name("Id").Value(page.Id.ToString());
    var submit = Div.Style("margin-top", "20px").Append(Button.Class("uk-button uk-button-danger").Append("Delete Page"));

    var form = Form
               .Attribute("method", "post")
               .Attribute("action", $"/delete-page")
               .Attribute("onsubmit", $"return confirm('Please confirm to delete this page');")
                 .Append(antiForgeryField)
                 .Append(id)
                 .Append(submit);

    return form.ToHtmlString();
}

static string RenderPageAttachmentsForEdit(Page page, AntiforgeryTokenSet antiForgery)
{
    if (page.Attachments.Count == 0)
        return string.Empty;

    var label = Span.Class("uk-label").Append("Attachments");
    var list = Ul.Class("uk-list");

    HtmlTag CreateEditorHelper(Attachment attachment) =>
      Span.Class("uk-inline")
          .Append(Span.Class("uk-form-icon").Attribute("uk-icon", "icon: copy"))
          .Append(Input.Text.Value($"[{attachment.FileName}](/attachment?fileId={attachment.FileId})")
            .Class("uk-input uk-form-small uk-form-width-large")
            .Style("cursor", "pointer")
            .Attribute("onclick", "copyMarkdownLink(this);")
          );

    static HtmlTag CreateDelete(int pageId, string attachmentId, AntiforgeryTokenSet antiForgery)
    {
        var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);
        var id = Input.Hidden.Name("Id").Value(attachmentId.ToString());
        var name = Input.Hidden.Name("PageId").Value(pageId.ToString());

        var submit = Button.Class("uk-button uk-button-danger uk-button-small").Append(Span.Attribute("uk-icon", "icon: close; ratio: .75;"));
        var form = Form
               .Style("display", "inline")
               .Attribute("method", "post")
               .Attribute("action", $"/delete-attachment")
               .Attribute("onsubmit", $"return confirm('Please confirm to delete this attachment');")
                 .Append(antiForgeryField)
                 .Append(id)
                 .Append(name)
                 .Append(submit);

        return form;
    }

    foreach (var attachment in page.Attachments)
    {
        list = list.Append(Li
          .Append(CreateEditorHelper(attachment))
          .Append(CreateDelete(page.Id, attachment.FileId, antiForgery))
        );
    }
    return label.ToHtmlString() + list.ToHtmlString();
}

static string RenderPageAttachments(Page page)
{
    if (page.Attachments.Count == 0)
        return string.Empty;

    var label = Span.Class("uk-label").Append("Attachments");
    var list = Ul.Class("uk-list uk-list-disc");
    foreach (var attachment in page.Attachments)
    {
        list = list.Append(Li.Append(A.Href($"/attachment?fileId={attachment.FileId}").Append(attachment.FileName)));
    }
    return label.ToHtmlString() + list.ToHtmlString();
}

// Build the wiki input form 
static string BuildForm(PageInput input, string path, AntiforgeryTokenSet antiForgery, ModelStateDictionary? modelState = null)
{
    bool IsFieldOK(string key) => modelState!.ContainsKey(key) && modelState[key]!.ValidationState == ModelValidationState.Invalid;

    var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);

    var nameField = Div
      .Append(Label.Class("uk-form-label").Append(nameof(input.Name)))
      .Append(Div.Class("uk-form-controls")
        .Append(Input.Text.Class("uk-input").Name("Name").Value(input.Name))
      );

    var contentField = Div
      .Append(Label.Class("uk-form-label").Append(nameof(input.Content)))
      .Append(Div.Class("uk-form-controls")
        .Append(Textarea.Name("Content").Class("uk-textarea").Append(input.Content))
      );

    var attachmentField = Div
      .Append(Label.Class("uk-form-label").Append(nameof(input.Attachment)))
      .Append(Div.Attribute("uk-form-custom", "target: true")
        .Append(Input.File.Name("Attachment"))
        .Append(Input.Text.Class("uk-input uk-form-width-large").Attribute("placeholder", "Click to select file").ToggleAttribute("disabled", true))
      );

    if (modelState is object && !modelState.IsValid)
    {
        if (IsFieldOK("Name"))
        {
            foreach (var er in modelState["Name"]!.Errors)
            {
                nameField = nameField.Append(Div.Class("uk-form-danger uk-text-small").Append(er.ErrorMessage));
            }
        }

        if (IsFieldOK("Content"))
        {
            foreach (var er in modelState["Content"]!.Errors)
            {
                contentField = contentField.Append(Div.Class("uk-form-danger uk-text-small").Append(er.ErrorMessage));
            }
        }
    }

    var submit = Div.Style("margin-top", "20px").Append(Button.Class("uk-button uk-button-primary").Append("Submit"));

    var form = Form
               .Class("uk-form-stacked")
               .Attribute("method", "post")
               .Attribute("enctype", "multipart/form-data")
               .Attribute("action", $"/{path}")
                 .Append(antiForgeryField)
                 .Append(nameField)
                 .Append(contentField)
                 .Append(attachmentField);

    if (input.Id is object)
    {
        HtmlTag id = Input.Hidden.Name("Id").Value(input.Id.ToString()!);
        form = form.Append(id);
    }

    form = form.Append(submit);

    return form.ToHtmlString();
}

static string BuildAuthForm(bool isLogin, AntiforgeryTokenSet antiForgery, ModelStateDictionary? modelState = null, LoginInput? login = null, RegisterInput? register = null, string err = "")
{
    bool IsFieldOK(string key) => modelState!.ContainsKey(key) && modelState[key]!.ValidationState == ModelValidationState.Invalid;

    string path = isLogin ? "login" : "register";
    string name = "";
    string password = "";
    string confirmPassword = "";

    if(login != null)
    {
        name = login.Name;
        password = login.Password;
    }
    if (register != null)
    {
        name = register.Name;
        password = register.Password;
        confirmPassword = register.ConfirmPassword;
    }

    var antiForgeryField = Input.Hidden.Name(antiForgery.FormFieldName).Value(antiForgery.RequestToken!);

    var nameField = Div
      .Append(Label.Class("uk-form-label").Append("Username"))
      .Append(Div.Class("uk-form-controls")
        .Append(Input.Text.Class("uk-input").Name("username").Attribute("placeholder ", "Username").Style("margin-bottom", "8px").Value(name))
      );

    var passwordField = Div
      .Append(Label.Class("uk-form-label").Append("Password"))
      .Append(Div.Class("uk-form-controls")
        .Append(Input.Text.Class("uk-input").Name("password").Attribute("placeholder ", "Password").Attribute("type", "password").Style("margin-bottom", "8px").Value(password))
      );    

    var confirmPasswordField = Div
      .Append(Label.Class("uk-form-label").Append("Confirm Password"))
      .Append(Div.Class("uk-form-controls")
        .Append(Input.Text.Class("uk-input").Name("confirmPassword").Attribute("placeholder ", "Confirm Password").Attribute("type", "password").Value(confirmPassword))
      );

    var toLoginLink = Div
        .Style("margin-top", "8px")
        .Append("already Registered? ")
        .Append(A.Class("to-login-btn").Append("Login").Href("/auth/login"));

    var toRegisterLink = Div
        .Style("margin-top", "8px")
        .Append("Don't have an account? ")
        .Append(A.Class("to-login-btn").Append("Register").Href("/auth/register"));

    var problemMessage = Div.Class("uk-form-danger uk-text-small").Append(err);

    if (modelState is object && !modelState.IsValid)
    {
        if (IsFieldOK("Name"))
        {
            foreach (var er in modelState["Name"]!.Errors)
            {
                nameField = nameField.Append(Div.Class("uk-form-danger uk-text-small").Append(er.ErrorMessage));
            }
        }

        if (IsFieldOK("Password"))
        {
            foreach (var er in modelState["Password"]!.Errors)
            {
                passwordField = passwordField.Append(Div.Class("uk-form-danger uk-text-small").Append(er.ErrorMessage));
            }
        }

        if (IsFieldOK("ConfirmPassword") && !isLogin)
        {
            foreach (var er in modelState["ConfirmPassword"]!.Errors)
            {
                confirmPasswordField = confirmPasswordField.Append(Div.Class("uk-form-danger uk-text-small").Append(er.ErrorMessage));
            }
        }
    }

    if (isLogin)
    {
        var submit = Div.Style("margin-top", "20px").Append(Button.Class("uk-button uk-button-primary").Append("Login"));
        var form = Form
               .Class("uk-form-stacked login-card")
               .Attribute("method", "post")
               .Attribute("action", $"/auth/{path}")
                 .Append(H1.Append("Login"))
                 .Append(antiForgeryField)
                 .Append(nameField)
                 .Append(passwordField)
                 .Append(toRegisterLink)
                 .Append(problemMessage)
                 .Append(submit);          
        return form.ToHtmlString();
    }
    else
    {
        var submit = Div.Style("margin-top", "20px").Append(Button.Class("uk-button uk-button-primary").Append("Register"));
        var form = Form
           .Class("uk-form-stacked login-card")
           .Attribute("method", "post")
           .Attribute("action", $"/auth/{path}")
             .Append(H1.Append("Register"))
             .Append(antiForgeryField)
             .Append(nameField)
             .Append(passwordField)
             .Append(confirmPasswordField)
             .Append(toLoginLink)
             .Append(problemMessage)
             .Append(submit);
        return form.ToHtmlString();
    }

    
}
static bool isLoggedIn(HttpContext context)
{
    return context.User.Identity?.IsAuthenticated ?? false;
}
class Render
{
    static string KebabToNormalCase(string txt) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(txt.Replace('-', ' '));

    static string[] MarkdownEditorHead() => new[]
    {
      @"<link rel=""stylesheet"" href=""https://unpkg.com/easymde/dist/easymde.min.css"">",
      @"<script src=""https://unpkg.com/easymde/dist/easymde.min.js""></script>"
    };

    static string[] MarkdownEditorFoot() => new[]
    {
      @"<script>
        var easyMDE = new EasyMDE({
          insertTexts: {
            link: [""["", ""]()""]
          }
        });

        function copyMarkdownLink(element) {
          element.select();
          document.execCommand(""copy"");
        }
        </script>"
    };

    (Template head, Template body, Template authBody,Template layout) _templates = (
      head: Scriban.Template.Parse(
        """
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{{ title }}</title>
          <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/css/uikit.min.css" />
          <link rel="stylesheet" href="styles.css">
          <link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" rel="stylesheet"
          integrity="sha384-QWTKZyjpPEjISv5WaRU9OFeRpok6YctnYmDr5pNlyT2bRjXh0JMhjY6hW+ALEwIH" crossorigin="anonymous">

          <script src="https://cdn.jsdelivr.net/npm/@popperjs/core@2.9.2/dist/umd/popper.min.js"
              integrity="sha384-IQsoLXl5PILFhosVNubq5LC7Qb9DXgDA9i+tQ8Zj3iwWAwPtgFTxbJ8NT4GN1R8p"
              crossorigin="anonymous"></script>
          <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.0.2/dist/js/bootstrap.min.js"
              integrity="sha384-cVKIPhGWiC2Al4u+LWgxfKTRIcfu0JTxR+EQDz/bgldoEyl4H0zUF0QKbrJ0EcQF"
              crossorigin="anonymous"></script>

          {{ header }}
          <style>
            .last-modified { font-size: small; }
            a:visited { color: blue; }
            a:link { color: red; }
            .login-container {
              display: flex;
              justify-content: center;
              align-items: center;
              height: 100vh;
          }
          .login-card{
            border: 1px solid lightgray;
            border-radius: 10px;
            display: flex;
            flex-direction: column;
            width: 500px;
            max-width: 90%;
            padding: 1rem;
            box-shadow: 0 8px 16px 0 rgba(0,0,0,0.3);
          }
          .to-login-btn, .to-register-btn{
              border: none;
              background-color: transparent;
              padding: 0;
              color: rgb(13, 110, 253);
              text-decoration: none;
          }

          .to-login-btn:hover, .to-register-btn:hover{
              text-decoration: underline;
          }
          </style>
          """),
      body: Scriban.Template.Parse("""
                <nav class="uk-navbar-container">
                  <div class="uk-container">
                    <div class="uk-navbar">
                      <div class="uk-navbar-left">
                        <ul class="uk-navbar-nav">
                          <li class="uk-active"><a href="/"><span uk-icon="home"></span></a></li>
                        </ul>
                      </div>
                      {{ if is_logged_in == true }}
                      <div class="uk-navbar-center">
                        <div class="uk-navbar-item">
                          <form action="/new-page">
                            <input class="uk-input uk-form-width-large" type="text" name="pageName" placeholder="Type desired page title here"></input>
                            <input type="submit"  class="uk-button uk-button-default" value="Add New Page">
                          </form>
                        </div>
                      </div>

                      <div class="uk-navbar-right">
                        <div class="uk-navbar-item">
                            <form method="post" action="/auth/logout">
                                <input type="submit" class="uk-button uk-button-primary" value="Logout">
                            </form>
                        </div>
                      </div>
                      {{ else }}
                        <div class="uk-navbar-right">
                            <div class="uk-navbar-item">
                                <a style="color: white;" href="/auth/login" class="uk-button uk-button-primary">Login</a>
                            </div>
                            <div class="uk-navbar-item">
                                <a style="color: white;" href="/auth/register" class="uk-button uk-button-primary">Register</a>
                            </div>
                      </div>
                      {{ end }}
                    </div>
                  </div>
                </nav>
                {{ if at_side_panel != "" }}
                  <div class="uk-container">
                  <div uk-grid>
                    <div class="uk-width-4-5">
                      <h1>{{ page_name }}</h1>
                      {{ content }}
                    </div>
                    <div class="uk-width-1-5">
                      {{ at_side_panel }}
                    </div>
                  </div>
                  </div>
                {{ else }}
                  <div class="uk-container">
                    <h1>{{ page_name }}</h1>
                    {{ content }}
                  </div>
                {{ end }}
                      
                <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit.min.js"></script>
                <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit-icons.min.js"></script>
                {{ at_foot }}
                
          """),
        authBody: Scriban.Template.Parse("""
                <main class="login-container">
                  {{content}}
                 </main>
                <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit.min.js"></script>
                <script src="https://cdn.jsdelivr.net/npm/uikit@3.19.4/dist/js/uikit-icons.min.js"></script>
                
          """),
      layout: Scriban.Template.Parse("""
                <!DOCTYPE html>
                  <head>
                    {{ head }}
                  </head>
                  <body>
                    {{ body }}
                  </body>
                </html>
          """)
    );

    // Use only when the page requires editor
    public HtmlString BuildEditorPage(string title, Func<IEnumerable<string>> atBody, Func<IEnumerable<string>>? atSidePanel = null) =>
      BuildPage(
        title,
        atHead: () => MarkdownEditorHead(),
        atBody: atBody,
        atSidePanel: atSidePanel,
        atFoot: () => MarkdownEditorFoot(),
        isLoggedIn: true
        );

    // General page layout building function
    public HtmlString BuildPage(string title, Func<IEnumerable<string>>? atHead = null, Func<IEnumerable<string>>? atBody = null, Func<IEnumerable<string>>? atSidePanel = null, Func<IEnumerable<string>>? atFoot = null, bool isLoggedIn = false)
    {
        var head = _templates.head.Render(new
        {
            title,
            header = string.Join("\r", atHead?.Invoke() ?? new[] { "" })
        });

        if(title == "login-page" || title == "register-page")
        {
            var body = _templates.authBody.Render(new
            {
                Content = string.Join("\r", atBody?.Invoke() ?? new[] { "" }),
            });

            return new HtmlString(_templates.layout.Render(new { head, body }));
        }
        else
        {
            var body = _templates.body.Render(new
            {
                PageName = KebabToNormalCase(title),
                Content = string.Join("\r", atBody?.Invoke() ?? new[] { "" }),
                AtSidePanel = string.Join("\r", atSidePanel?.Invoke() ?? new[] { "" }),
                AtFoot = string.Join("\r", atFoot?.Invoke() ?? new[] { "" }),
                IsLoggedIn = isLoggedIn
            });

            return new HtmlString(_templates.layout.Render(new { head, body }));
        }
    }
}

class Wiki
{
    DateTime Timestamp() => DateTime.UtcNow;

    const string PageCollectionName = "Pages";
    const string UserCollectionName = "Users";
    const string AllPagesKey = "AllPages";
    const double CacheAllPagesForMinutes = 30;

    readonly IWebHostEnvironment _env;
    readonly IMemoryCache _cache;
    readonly ILogger _logger;

    public Wiki(IWebHostEnvironment env, IMemoryCache cache, ILogger<Wiki> logger)
    {
        _env = env;
        _cache = cache;
        _logger = logger;
    }

    // Get the location of the LiteDB file.
    string GetDbPath() => Path.Combine(_env.ContentRootPath, "wiki.db");

    // List all the available wiki pages. It is cached for 30 minutes.
    public List<Page> ListAllPages()
    {
        var pages = _cache.Get(AllPagesKey) as List<Page>;

        if (pages is object)
            return pages;

        using var db = new LiteDatabase(GetDbPath());
        var coll = db.GetCollection<Page>(PageCollectionName);
        var items = coll.Query().ToList();

        _cache.Set(AllPagesKey, items, new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromMinutes(CacheAllPagesForMinutes)));
        return items;
    }

    // Get a wiki page based on its path
    public Page? GetPage(string path)
    {
        using var db = new LiteDatabase(GetDbPath());
        var coll = db.GetCollection<Page>(PageCollectionName);
        coll.EnsureIndex(x => x.Name);

        return coll.Query()
                .Where(x => x.Name.Equals(path, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
    }

    // Save or update a wiki page. Cache(AllPagesKey) will be destroyed.
    public (bool isOk, Page? page, Exception? ex) SavePage(PageInput input)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<Page>(PageCollectionName);
            coll.EnsureIndex(x => x.Name);

            Page? existingPage = input.Id.HasValue ? coll.FindOne(x => x.Id == input.Id) : null;

            var sanitizer = new HtmlSanitizer();
            var properName = input.Name.ToString().Trim().Replace(' ', '-').ToLower();

            Attachment? attachment = null;
            if (!string.IsNullOrWhiteSpace(input.Attachment?.FileName))
            {
                attachment = new Attachment
                (
                    FileId: Guid.NewGuid().ToString(),
                    FileName: input.Attachment.FileName,
                    MimeType: input.Attachment.ContentType,
                    LastModifiedUtc: Timestamp()
                );

                using var stream = input.Attachment.OpenReadStream();
                var res = db.FileStorage.Upload(attachment.FileId, input.Attachment.FileName, stream);
            }

            if (existingPage is not object)
            {
                var newPage = new Page
                {
                    Name = sanitizer.Sanitize(properName),
                    Content = input.Content, //Do not sanitize on input because it will impact some markdown tag such as >. We do it on the output instead.
                    LastModifiedUtc = Timestamp()
                };

                if (attachment is object)
                    newPage.Attachments.Add(attachment);

                coll.Insert(newPage);

                _cache.Remove(AllPagesKey);
                return (true, newPage, null);
            }
            else
            {
                var updatedPage = existingPage with
                {
                    Name = sanitizer.Sanitize(properName),
                    Content = input.Content, //Do not sanitize on input because it will impact some markdown tag such as >. We do it on the output instead.
                    LastModifiedUtc = Timestamp()
                };

                if (attachment is object)
                    updatedPage.Attachments.Add(attachment);

                coll.Update(updatedPage);

                _cache.Remove(AllPagesKey);
                return (true, updatedPage, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"There is an exception in trying to save page name '{input.Name}'");
            return (false, null, ex);
        }
    }

    public (bool isOk, Page? p, Exception? ex) DeleteAttachment(int pageId, string id)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<Page>(PageCollectionName);
            var page = coll.FindById(pageId);
            if (page is not object)
            {
                _logger.LogWarning($"Delete attachment operation fails because page id {id} cannot be found in the database");
                return (false, null, null);
            }

            if (!db.FileStorage.Delete(id))
            {
                _logger.LogWarning($"We cannot delete this file attachment id {id} and it's a mystery why");
                return (false, page, null);
            }

            page.Attachments.RemoveAll(x => x.FileId.Equals(id, StringComparison.OrdinalIgnoreCase));

            var updateResult = coll.Update(page);

            if (!updateResult)
            {
                _logger.LogWarning($"Delete attachment works but updating the page (id {pageId}) attachment list fails");
                return (false, page, null);
            }

            return (true, page, null);
        }
        catch (Exception ex)
        {
            return (false, null, ex);
        }
    }

    public (bool isOk, Exception? ex) DeletePage(int id, string homePageName)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<Page>(PageCollectionName);

            var page = coll.FindById(id);

            if (page is not object)
            {
                _logger.LogWarning($"Delete operation fails because page id {id} cannot be found in the database");
                return (false, null);
            }

            if (page.Name.Equals(homePageName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning($"Page id {id}  is a home page and delete operation on home page is not allowed");
                return (false, null);
            }

            //Delete all the attachments
            foreach (var a in page.Attachments)
            {
                db.FileStorage.Delete(a.FileId);
            }

            if (coll.Delete(id))
            {
                _cache.Remove(AllPagesKey);
                return (true, null);
            }

            _logger.LogWarning($"Somehow we cannot delete page id {id} and it's a mistery why.");
            return (false, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }

    // Return null if file cannot be found.
    public (LiteFileInfo<string> meta, byte[] file)? GetFile(string fileId)
    {
        using var db = new LiteDatabase(GetDbPath());

        var meta = db.FileStorage.FindById(fileId);
        if (meta is not object)
            return null;

        using var stream = new MemoryStream();
        db.FileStorage.Download(fileId, stream);
        return (meta, stream.ToArray());
    }

    public (bool isOk, User? user, Exception? ex) CanLogin(LoginInput input)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<User>(UserCollectionName);
            coll.EnsureIndex(x => x.Name);

            var user = coll.FindOne(x => x.Name == input.Name);

            if (user is null)
            {
                return (false, null, new Exception("No user exists with this name"));
            }
            else if (user.Password != input.Password)
            {
                return (false, null, new Exception("Wrong password"));
            }
            return (true, user, null);
        }
        catch (Exception ex) 
        {
            return (false, null, ex);
        }
    }

    public (bool isOk, Exception? ex) RegisterUser(RegisterInput input)
    {
        try
        {
            using var db = new LiteDatabase(GetDbPath());
            var coll = db.GetCollection<User>(UserCollectionName);
            coll.EnsureIndex(x => x.Name);

            if(coll.Exists(x=> x.Name == input.Name))
            {
                return (false, new Exception("Username is Taken"));
            }

            var user = new User
            {
                Name = input.Name,
                Password = input.Password
            };

            coll.Insert(user);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex);
        }
    }
}

record Page
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public DateTime LastModifiedUtc { get; set; }

    public List<Attachment> Attachments { get; set; } = new();
}

record User
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

}

record Attachment
(
    string FileId,

    string FileName,

    string MimeType,

    DateTime LastModifiedUtc
);

record PageInput(int? Id, string Name, string Content, IFormFile? Attachment)
{
    public static PageInput From(IFormCollection form)
    {
        var (id, name, content) = (form["Id"], form["Name"], form["Content"]);

        int? pageId = null;

        if (!StringValues.IsNullOrEmpty(id))
            pageId = Convert.ToInt32(id);

        IFormFile? file = form.Files["Attachment"];

        return new PageInput(pageId, name!, content!, file);
    }
}

record LoginInput(string Name, string Password)
{
    public static LoginInput From(IFormCollection form)
    {
        var (username, password) = (form["username"], form["password"]);
        return new LoginInput(username!, password!);
    }
}

record RegisterInput(string Name, string Password, string ConfirmPassword)
{
    public static RegisterInput From(IFormCollection form)
    {
        var (username, password, confirmPassword) = (form["username"], form["password"], form["confirmPassword"]);
        return new RegisterInput(username!, password!, confirmPassword!);
    }
}

class PageInputValidator : AbstractValidator<PageInput>
{
    public PageInputValidator(string pageName, string homePageName)
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name is required");
        if (pageName.Equals(homePageName, StringComparison.OrdinalIgnoreCase))
            RuleFor(x => x.Name).Must(name => name.Equals(homePageName)).WithMessage($"You cannot modify home page name. Please keep it {homePageName}");

        RuleFor(x => x.Content).NotEmpty().WithMessage("Content is required");
    }
}

class LoginInputValidator : AbstractValidator<LoginInput>
{
    public LoginInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Username is required");

        RuleFor(x => x.Password)
            .NotEmpty()
            .WithMessage("Password is required")
            .MinimumLength(8)
            .WithMessage("Password must be at least 8 characters long");
    }
}

class RegisterInputValidator : AbstractValidator<RegisterInput>
{
    public RegisterInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Username is required");

        RuleFor(x => x.Password)
            .Cascade(CascadeMode.Stop)
            .NotEmpty()
            .WithMessage("Password is required")
            .MinimumLength(8)
            .WithMessage("Password must be at least 8 characters long");

        RuleFor(x => x.ConfirmPassword)
            .NotEmpty()
            .WithMessage("Confirm Password is required");

        RuleFor(x => x)
            .Must(x => x.Password == x.ConfirmPassword)
            .WithMessage("Passwords do not match")
            .When(x => !string.IsNullOrEmpty(x.Password) && !string.IsNullOrEmpty(x.ConfirmPassword));
    }
}