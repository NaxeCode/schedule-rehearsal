"""Exercise the recorded visitor flow at desktop and mobile widths with Playwright."""
import argparse
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Thread

from playwright.sync_api import expect, sync_playwright


class QuietFiles(SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass


def exercise(page, url):
    errors = []
    page.on("pageerror", lambda error: errors.append(str(error)))
    page.goto(url)
    for scenario, moves, callbacks in [("morning", 2, 6), ("overnight", 1, 4)]:
        page.get_by_label("Choose a rehearsal").select_option(scenario)
        approve = page.get_by_role("button", name=f"Approve {moves} move" + ("s" if moves > 1 else ""), exact=True)
        approve.click()
        page.get_by_role("button", name="Interrupt worker").click()
        expect(page.get_by_role("heading", name="Triggers after interruption")).to_be_visible()
        page.get_by_role("button", name="Restart and replay").click()
        expect(page.get_by_role("heading", name="Firing ledger")).to_be_visible()
        assert page.locator("#rows tbody tr").count() == callbacks
        page.get_by_role("button", name="Replay again").click()
        expect(page.get_by_role("heading", name="Replay added 0 callbacks")).to_be_visible()
        page.get_by_role("button", name="Start over").click()
        fixed = "Settlement export" if scenario == "morning" else "Archive handoff"
        local = "Dispatch briefing" if scenario == "morning" else "Night shift handover"
        for policy, description in [("wall", fixed), ("keep", local), ("intent", "Every pending job")]:
            page.get_by_label("Recorded policy").select_option(policy)
            expect(page.locator("#policy-summary")).to_contain_text(description)
            assert page.evaluate("document.documentElement.scrollWidth <= innerWidth")
        assert page.request.get(page.locator("#receipt").get_attribute("href")).ok
    assert not errors, errors


def exercise_receipt_switch(page, url):
    page.goto(url)
    scenario = page.get_by_label("Choose a rehearsal")
    policy = page.get_by_label("Recorded policy")
    expect(page.locator("#policies")).to_contain_text("Dispatch briefing")
    page.get_by_role("button", name="Approve 2 moves").click()
    page.get_by_role("button", name="Interrupt worker").click()
    overnight = "**/receipts/overnight.json"
    pending = []
    page.route(overnight, lambda route: pending.append(route))
    with page.expect_request(overnight):
        scenario.select_option("overnight")
    expect(policy).to_be_disabled()
    expect(page.locator("#chart")).to_be_empty()
    expect(page.locator("#ledger")).to_be_hidden()
    expect(page.locator("#policies")).to_be_empty()
    expect(page.locator(".steps [aria-current]")).to_have_count(0)
    expect(page.locator("#receipt")).to_have_attribute("href", "receipts/overnight.json")
    pending.pop().abort()
    retry = page.get_by_role("button", name="Retry recording")
    expect(retry).to_be_visible()
    expect(page.locator("#policy-summary")).to_be_empty()
    expect(page.locator(".lede")).not_to_contain_text("dispatch")
    page.unroute(overnight)
    retry.click()
    expect(policy).to_be_enabled()
    expect(page.locator("#policies")).to_contain_text("Night shift handover")
    expect(page.get_by_role("button", name="Approve 1 move", exact=True)).to_be_visible()

    # Return to midnight while the morning recording is still in flight.
    morning = "**/receipts/morning.json"
    page.route(morning, lambda route: pending.append(route))
    with page.expect_request(morning):
        scenario.select_option("morning")
    scenario.select_option("overnight")
    expect(page.locator("#policies")).to_contain_text("Night shift handover")
    with page.expect_event("requestfinished", predicate=lambda request: request.url.endswith("/morning.json")):
        pending.pop().continue_()
    expect(scenario).to_have_value("overnight")
    expect(page.locator("#policies")).not_to_contain_text("Dispatch briefing")
    expect(page.locator("#receipt")).to_have_attribute("href", "receipts/overnight.json")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--channel", help="Use an installed Chrome instead of bundled Chromium, e.g. chrome.")
    args = parser.parse_args()
    web = Path(__file__).resolve().parents[1] / "web"
    server = ThreadingHTTPServer(("127.0.0.1", 0), partial(QuietFiles, directory=str(web)))
    thread = Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with sync_playwright() as pw:
            browser = pw.chromium.launch(channel=args.channel)
            url = f"http://127.0.0.1:{server.server_port}/"
            for width, height in [(1280, 900), (390, 844), (320, 700)]:
                page = browser.new_page(viewport={"width": width, "height": height}, base_url=url)
                exercise(page, url)
                print(f"{width}px: both scenarios, interruption, replay, reset and all three policies passed.")
                page.close()
            page = browser.new_page(base_url=url)
            exercise_receipt_switch(page, url)
            print("Receipt loading, failure, retry and an out-of-order response passed.")
            page.close()
            browser.close()
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
