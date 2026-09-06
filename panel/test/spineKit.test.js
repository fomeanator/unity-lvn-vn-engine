import { describe, expect, it } from "vitest";
import {
  actorLine, animationNames, atlasPages, groupSpineKits, kitActorId, kitMissing,
  looksLikeSkeleton, pagesPhrase, stripExt,
} from "../src/components/AdminAssets.jsx";

// Комплект Spine ломается тише всего: строка сценария верна, компилятор
// доволен, а на устройстве пусто. Здесь проверено ПРАВИЛО, по которому панель
// объявляет комплект полным, — на живом сервере такую ошибку видно только
// глазами и только по счастливой случайности.

const f = (name, size = 10) => ({ name, size, dir: false });

describe("stripExt", () => {
  it("примеряет .skel.bytes раньше .skel — иначе основа осталась бы с хвостом", () => {
    expect(stripExt("hero.skel.bytes", [".skel.bytes", ".skel"])).toBe("hero");
    expect(stripExt("hero.skel", [".skel.bytes", ".skel"])).toBe("hero");
  });
  it("не отрезает расширение у файла, который целиком им и назван", () => {
    expect(stripExt(".json", [".json"])).toBe(null);
  });
  it("чужое расширение не трогает", () => {
    expect(stripExt("bg.png", [".json"])).toBe(null);
  });
});

describe("groupSpineKits", () => {
  it("json рядом с атласом — это скелет", () => {
    const kits = groupSpineKits([f("hero.json"), f("hero.atlas.txt"), f("hero.png")], "art");
    expect(kits.length).toBe(1);
    expect(kits[0]).toMatchObject({ base: "hero", skeleton: "hero.json", atlas: "hero.atlas.txt", binary: false });
  });
  it("второе написание атласа (.atlas) считается тем же комплектом", () => {
    const kits = groupSpineKits([f("boy.json"), f("boy.atlas")], "art");
    expect(kits[0].atlas).toBe("boy.atlas");
  });
  it("двоичный скелет виден и без атласа — иначе о недостаче некому сказать", () => {
    const kits = groupSpineKits([f("hero.skel")], "art");
    expect(kits[0]).toMatchObject({ base: "hero", skeleton: "hero.skel", binary: true, atlas: "" });
  });
  it("одинокий json в обычной папке скелетом не объявляется", () => {
    expect(groupSpineKits([f("manifest.json"), f("chapter1.json")], "art")).toEqual([]);
  });
  it("в spine/<id>/ одинокий json — скелет: так кладёт выгрузки сам сервер", () => {
    const kits = groupSpineKits([f("hero.json")], "spine/hero");
    expect(kits.map((k) => k.base)).toEqual(["hero"]);
  });
  it("названный вызывающим скелет считается скелетом в любой папке", () => {
    const kits = groupSpineKits([f("hero.json")], "art", ["hero"]);
    expect(kits[0].skeleton).toBe("hero.json");
  });
  it("лежат оба написания — берётся json: по нему видны имена анимаций", () => {
    const kits = groupSpineKits([f("hero.skel"), f("hero.json"), f("hero.atlas.txt")], "art");
    expect(kits[0]).toMatchObject({ skeleton: "hero.json", binary: false });
  });
  it("атлас без скелета — тоже комплект, но неполный", () => {
    const kits = groupSpineKits([f("hero.atlas.txt")], "art");
    expect(kits[0]).toMatchObject({ base: "hero", skeleton: "", atlas: "hero.atlas.txt" });
  });
});

describe("atlasPages", () => {
  const atlas = [
    "", "hero.png", "size: 1024,1024", "format: RGBA8888", "filter: Linear,Linear", "repeat: none",
    "head", "  rotate: false", "  xy: 2, 2", "", "hero2.png", "size: 512,512", "arm", "  xy: 4, 4",
  ].join("\n");
  it("страницы называет сам атлас — все, а не только первую", () => {
    expect(atlasPages(atlas)).toEqual(["hero.png", "hero2.png"]);
  });
  it("на пустом тексте не выдумывает страниц", () => {
    expect(atlasPages("")).toEqual([]);
    expect(atlasPages(null)).toEqual([]);
  });
  it("jpg-страница читается наравне с png", () => {
    expect(atlasPages("pack.jpg\nsize: 8,8\n")).toEqual(["pack.jpg"]);
  });
});

describe("kitMissing", () => {
  const full = { base: "hero", skeleton: "hero.json", atlas: "hero.atlas.txt", pages: ["hero.png"] };
  it("полный комплект не придумывает недостачи", () => {
    expect(kitMissing(full, ["hero.json", "hero.atlas.txt", "hero.png"])).toEqual([]);
  });
  it("забытый атлас назван по имени, которое надо долить", () => {
    expect(kitMissing({ ...full, atlas: "" }, ["hero.json"])).toContain("hero.atlas.txt");
  });
  it("страница, которую назвал атлас, но которой нет на диске", () => {
    expect(kitMissing(full, ["hero.json", "hero.atlas.txt"])).toEqual(["hero.png"]);
  });
  it("несовпадение регистра — не «файла нет», а именно регистр: Linux не простит", () => {
    const missing = kitMissing(full, ["hero.json", "hero.atlas.txt", "Hero.png"]);
    expect(missing.length).toBe(1);
    expect(missing[0]).toContain("Hero.png");
    expect(missing[0]).toMatch(/регистр/);
  });
  it("страница из соседней папки не объявляется пропажей — её здесь не смотрели", () => {
    expect(kitMissing({ ...full, pages: ["sub/hero.png"] }, ["hero.json", "hero.atlas.txt"])).toEqual([]);
  });
});

describe("animationNames", () => {
  it("читает ключи объекта animations", () => {
    expect(animationNames('{"animations":{"idle":{},"walk":{}}}')).toEqual(["idle", "walk"]);
  });
  it("на нечитаемом скелете возвращает пусто, а не догадку", () => {
    expect(animationNames("<<binary>>")).toEqual([]);
    expect(animationNames('{"animations":[]}')).toEqual([]);
    expect(animationNames("{}")).toEqual([]);
  });
});

describe("looksLikeSkeleton", () => {
  it("узнаёт заголовок выгрузки Spine по первым килобайтам", () => {
    expect(looksLikeSkeleton('{"skeleton":{"hash":"x","spine":"4.1.24"},"bones":[{"name":"root"}]')).toBe(true);
  });
  it("обычный json скелетом не объявляет", () => {
    expect(looksLikeSkeleton('{"titles":[{"id":"a"}]}')).toBe(false);
    expect(looksLikeSkeleton("")).toBe(false);
  });
});

describe("actorLine", () => {
  it("собирает строку ровно в том виде, в каком её читает движок", () => {
    expect(actorLine("hero", "/content/spine/hero/hero.json", "idle"))
      .toBe('actor id=hero spine="/content/spine/hero/hero.json" play="idle"');
  });
  it("без имени анимации play= не пишется: выдуманное имя хуже отсутствующего", () => {
    expect(actorLine("hero", "/content/spine/hero/hero.skel", ""))
      .toBe('actor id=hero spine="/content/spine/hero/hero.skel"');
  });
  it("имя с пробелом берётся в кавычки — иначе строка распадётся надвое", () => {
    expect(actorLine("my hero", "/content/a/b.json", "")).toBe('actor id="my hero" spine="/content/a/b.json"');
  });
});

describe("kitActorId", () => {
  const kit = { base: "skeleton" };
  it("единственный комплект зовут по папке — выгрузка часто зовёт файл skeleton.json", () => {
    expect(kitActorId(kit, "spine/hero", 1)).toBe("hero");
  });
  it("папка spine/ героев не различает — тогда имя даёт файл", () => {
    expect(kitActorId(kit, "spine", 1)).toBe("skeleton");
  });
  it("несколько комплектов в папке — имя тоже от файла", () => {
    expect(kitActorId(kit, "spine/cast", 2)).toBe("skeleton");
  });
});

describe("pagesPhrase", () => {
  it("считает по-русски", () => {
    expect(pagesPhrase(1)).toBe("1 страница");
    expect(pagesPhrase(3)).toBe("3 страницы");
    expect(pagesPhrase(5)).toBe("5 страниц");
    expect(pagesPhrase(11)).toBe("11 страниц");
    expect(pagesPhrase(21)).toBe("21 страница");
  });
});
