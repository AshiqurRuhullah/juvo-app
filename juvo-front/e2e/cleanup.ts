import { test as cleanup } from "@playwright/test";
import { dropDatabase } from "./support/env";

/** Drops the throw-away database after the whole run (also when tests fail). */
cleanup("drop the e2e database", async () => {
  dropDatabase();
});
