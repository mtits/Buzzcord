import XCTest
@testable import Buzzcord

// Port of GameBoardTest in src/SelfTest.cs, plus the Mac game catalog.
final class GameTests: XCTestCase {
    func testBoardChanges() throws {
        let json = #"[{"id":"1","data":{"type":"played_games","games":[{"game_id":"10"}]}},{"id":"2","data":{"type":"favorite_games","games":[{"game_id":"20","comment":"goat","tags":["expert"]}]}},{"id":"3","data":{"type":"current_games","games":[{"game_id":"31"},{"game_id":"32"},{"game_id":"33"},{"game_id":"34"},{"game_id":"35"}]}},{"id":"4","data":{"type":"application","application_id":"99"}}]"#
        var widgets = try XCTUnwrap(Json.parse(Data(json.utf8)) as? [Any])

        XCTAssertNil(GameBoard.change(&widgets, type: "played_games", appId: "11", add: true), "add to Games I Like")
        let liked = GameBoard.entries(widgets, "played_games")
        XCTAssertEqual(liked.map { $0.str("game_id") }, ["11", "10"], "new game should go first")
        XCTAssertNotNil(GameBoard.change(&widgets, type: "played_games", appId: "11", add: true), "duplicate should be refused")
        XCTAssertNil(GameBoard.change(&widgets, type: "favorite_games", appId: "21", add: true))
        XCTAssertEqual(GameBoard.entries(widgets, "favorite_games").map { $0.str("game_id") }, ["21"], "favorite should be replaced, not added")
        XCTAssertNotNil(GameBoard.change(&widgets, type: "current_games", appId: "36", add: true), "rotation holds 5")
        XCTAssertNil(GameBoard.change(&widgets, type: "want_to_play_games", appId: "40", add: true))
        XCTAssertEqual(GameBoard.entries(widgets, "want_to_play_games").count, 1, "new list created")
        XCTAssertNil(GameBoard.change(&widgets, type: "played_games", appId: "10", add: false))
        XCTAssertEqual(GameBoard.entries(widgets, "played_games").count, 1, "remove")
        XCTAssertEqual(GameBoard.gameIds(widgets).count, 8)
        XCTAssertEqual((widgets[0] as? JObj)?.str("id"), "1", "widget ids are kept for the save")
        XCTAssertEqual(widgets.count, 5)
        XCTAssertEqual((widgets[3] as? JObj)?.obj("data")?.str("application_id"), "99", "other widgets must be kept")
        XCTAssertEqual(GameBoard.tagText("hidden_gem"), "Hidden gem")
    }

    func testCatalog() {
        let c = GameCatalog()
        c.parse("n\t1\tHearthstone\nn\t2\tWorld of Warcraft\nn\t3\tHearts\nn\t4\tTeam Fortress 2\n"
                + "x\t1\thearthstone.app\nx\t2\t_retail_/world of warcraft.app\nx\t4\thl2_osx\n")
        XCTAssertEqual(c.search("hear").map(\.name), ["Hearts", "Hearthstone"])
        XCTAssertEqual(c.search("stone").map(\.name), ["Hearthstone"])
        XCTAssertEqual(c.id(named: "world of warcraft"), "2")
        XCTAssertEqual(c.match(path: "/Applications/Hearthstone/Hearthstone.app/Contents/MacOS/Hearthstone"), "1")
        XCTAssertEqual(c.match(path: "/Applications/World of Warcraft/_retail_/World of Warcraft.app/Contents/MacOS/World of Warcraft"), "2")
        XCTAssertNil(c.match(path: "/Applications/World of Warcraft/_classic_/World of Warcraft.app/Contents/MacOS/World of Warcraft"))
        XCTAssertEqual(c.match(path: "/Users/x/Library/Application Support/Steam/steamapps/common/Team Fortress 2/hl2_osx"), "4")
        XCTAssertNil(c.match(path: "/Applications/Safari.app/Contents/MacOS/Safari"))
    }
}

extension GameTests {
    // A profile crashed when one game was in two lists (Favorite and Games I Like).
    func testSameGameInTwoLists() throws {
        let json = #"[{"data":{"type":"favorite_games","games":[{"game_id":"7"}]}},{"data":{"type":"played_games","games":[{"game_id":"7"},{"game_id":"8"}]}}]"#
        let widgets = try XCTUnwrap(Json.parse(Data(json.utf8)) as? [Any])
        XCTAssertEqual(GameBoard.gameIds(widgets), ["7", "7", "8"])
        XCTAssertEqual(GameBoard.uniqueIds(widgets), ["7", "8"])
    }
}
