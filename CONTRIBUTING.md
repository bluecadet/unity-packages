## Publishing Changes

1. Update the version number in the `package.json` file of the package you are modifying. Follow [Semantic Versioning](https://semver.org/) guidelines.

2. Commit your changes with a descriptive message.

3. Create a new tag for the version you are publishing. Use the format `package-name/vX.Y.Z`, where `X.Y.Z` is the version number. Exclude `com.bluecadet.` from the package name in the tag. For example, for `com.bluecadet.spring` version `1.2.3`, the tag should be `spring/v1.2.3`.
    ```sh
    git tag -a spring/v1.2.3
    ```

4. Push the commit and the tag to the remote repository:
   ```sh
   git push --tags
   ```